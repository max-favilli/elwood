-- All five keys share one cluster slot. No iteration over a whole stage occurs here.
-- Lua is atomic, not transactional rollback: validate before writing, then check
-- cardinality invariants on every subsequent call. Missing/corrupt state fails closed.
local op, definition = ARGV[1], ARGV[2]
local d, a = cjson.decode(definition), cjson.decode(ARGV[3])
local function stop(reason) error('ELWOOD_WORK_' .. reason, 0) end
local clock = redis.call('TIME')
local now = tonumber(clock[1]) * 1000 + math.floor(tonumber(clock[2]) / 1000)
local kinds = {'hash', 'hash', 'zset', 'hash', 'zset'}
for i = 1, 5 do
    local t = redis.call('TYPE', KEYS[i]).ok
    if t ~= 'none' and t ~= kinds[i] then stop('CORRUPT_TYPE') end
end
-- Explicit decimal format also works on protocol servers that serialize a Lua
-- double using scientific notation; PEXPIREAT accepts an integer, not a float.
local function expire(key) redis.call('PEXPIREAT', key, string.format('%.0f', d.expires)) end
local function header(field) return redis.call('HGET', KEYS[1], field) end
local existing = header('definition')
if not existing then
    if op ~= 'register' then stop('MISSING') end
    for i = 1, 5 do
        if redis.call('EXISTS', KEYS[i]) ~= 0 then stop('ORPHANED') end
    end
    if d.deadline <= now or d.deadline > now + 604800000 then stop('DEADLINE') end
    redis.call('HSET', KEYS[1], 'definition', definition, 'running', 0,
        'completed', 0, 'failed', 0, 'review', 0, 'retrying', 0, 'materialized', 0, 'sequence', 0)
    expire(KEYS[1])
    return 'true'
end
if existing ~= definition then stop('DEFINITION_CONFLICT') end
local function number(field)
    local n = tonumber(header(field))
    if not n or n < 0 or n ~= math.floor(n) then stop('CORRUPT_COUNTER') end
    return n
end
local running, completed, failed = number('running'), number('completed'), number('failed')
local review, retrying, materialized = number('review'), number('retrying'), number('materialized')
local sequence = number('sequence')
if materialized > d.itemCount or running > d.maxConcurrency
    or materialized ~= running + completed + failed + review + retrying
    or materialized ~= redis.call('HLEN', KEYS[2])
    or running ~= redis.call('ZCARD', KEYS[3])
    or completed ~= redis.call('HLEN', KEYS[4])
    or retrying ~= redis.call('ZCARD', KEYS[5]) then stop('CORRUPT_INDEX') end
for i = 1, 5 do
    if redis.call('EXISTS', KEYS[i]) == 1 and redis.call('PTTL', KEYS[i]) < 0 then stop('CORRUPT_EXPIRY') end
end
local function deadline() if now >= d.deadline then stop('DEADLINE') end end
local function incr(field, amount) redis.call('HINCRBY', KEYS[1], field, amount) end
local function nextFence()
    sequence = sequence + 1
    redis.call('HSET', KEYS[1], 'sequence', sequence)
    return sequence
end
local function field(ordinal) return string.format('%.0f', ordinal) end
local function read(ordinal)
    local id = field(ordinal)
    local raw = redis.call('HGET', KEYS[2], id)
    if not raw then return {ordinal = ordinal, status = 'Pending', attempt = 0} end
    local valid, item = pcall(cjson.decode, raw)
    if not valid or type(item) ~= 'table' or item.ordinal ~= ordinal
        or type(item.attempt) ~= 'number' or item.attempt < 1 or item.attempt > 100
        or item.attempt ~= math.floor(item.attempt)
        or type(item.fence) ~= 'number' or item.fence < 1 or item.fence > sequence
        or item.fence ~= math.floor(item.fence)
        or type(item.owner) ~= 'string' or type(item.token) ~= 'string'
        or type(item.callInFlight) ~= 'boolean' or type(item.mayWrite) ~= 'boolean'
        or type(item.uncertain) ~= 'boolean' or (item.mayWrite and not item.callInFlight)
        or type(item.checkpointSequence) ~= 'number' or item.checkpointSequence < 0
        or item.checkpointSequence ~= math.floor(item.checkpointSequence) then stop('CORRUPT_ITEM') end
    local lease = redis.call('ZSCORE', KEYS[3], id)
    local retry = redis.call('ZSCORE', KEYS[5], id)
    local result = redis.call('HGET', KEYS[4], id)
    if item.status == 'Running' then
        if not lease or tonumber(lease) ~= item.leaseUntil or retry or result then stop('CORRUPT_ITEM') end
    elseif item.status == 'Retrying' then
        if not retry or tonumber(retry) ~= item.notBefore or lease or result then stop('CORRUPT_ITEM') end
    elseif item.status == 'Completed' then
        if not result or result ~= item.resultReference or lease or retry then stop('CORRUPT_ITEM') end
    elseif item.status == 'Failed' or item.status == 'AwaitingReview' then
        if lease or retry or result then stop('CORRUPT_ITEM') end
    else stop('CORRUPT_ITEM') end
    return item
end
local function save(item)
    redis.call('HSET', KEYS[2], field(item.ordinal), cjson.encode(item))
    expire(KEYS[2])
end
local function owns(item)
    return item.owner == a.owner and item.token == a.token and item.fence == a.fence
end
local function active(item)
    if not owns(item) or item.status ~= 'Running' or item.leaseUntil <= now then stop('OWNERSHIP_LOST') end
end
local function release(item)
    redis.call('ZREM', KEYS[3], field(item.ordinal))
    incr('running', -1)
end
if op == 'register' then return 'false' end
if op == 'progress' then
    return cjson.encode({total = d.itemCount, running = running, completed = completed,
        failed = failed, awaitingReview = review, retrying = retrying,
        resultReference = header('result') or nil})
end
if op == 'page' then
    local page = {}
    for i = a.start, math.min(d.itemCount, a.start + a.count) - 1 do page[field(i)] = read(i) end
    return cjson.encode(page)
end
if op == 'recover' then
    local expired = redis.call('ZRANGEBYSCORE', KEYS[3], '-inf', string.format('%.0f', now), 'LIMIT', '0', string.format('%.0f', a.limit))
    local items = {}
    -- Read/validate the entire bounded batch before making any mutation.
    for _, ordinal in ipairs(expired) do
        local item = read(tonumber(ordinal))
        if item.status ~= 'Running' or item.leaseUntil > now then stop('CORRUPT_ITEM') end
        table.insert(items, item)
    end
    for _, item in ipairs(items) do
        item.status = 'AwaitingReview'
        item.uncertain = item.callInFlight == true and item.mayWrite == true
        item.diagnostic = 'Worker ownership expired; retry requires the configured recovery policy.'
        save(item)
        release(item)
        incr('review', 1)
    end
    return tostring(#items)
end
if op == 'aggregate' then
    if completed ~= d.itemCount or header('result') then return 'null' end
    deadline()
    local untilTime = tonumber(header('aggregationUntil') or '0')
    if untilTime > now then
        if header('aggregationOwner') == a.owner and header('aggregationToken') == a.token then
            return header('aggregationFence')
        end
        return 'null'
    end
    local fence = nextFence()
    redis.call('HSET', KEYS[1], 'aggregationOwner', a.owner, 'aggregationToken', a.token,
        'aggregationFence', fence, 'aggregationUntil', math.min(now + a.duration, d.deadline))
    return tostring(fence)
end
if op == 'aggregate-complete' or op == 'aggregate-renew' then
    local owned = header('aggregationOwner') == a.owner and header('aggregationToken') == a.token
        and tonumber(header('aggregationFence')) == a.fence
    local result = header('result')
    if result then
        if op == 'aggregate-complete' and owned and result == a.resultReference then return 'false' end
        stop('AGGREGATION_CONFLICT')
    end
    if not owned or tonumber(header('aggregationUntil') or '0') <= now then stop('OWNERSHIP_LOST') end
    if completed ~= d.itemCount then stop('NOT_COMPLETE') end
    if op == 'aggregate-renew' then
        redis.call('HSET', KEYS[1], 'aggregationUntil', math.min(now + a.duration, d.deadline))
        return 'true'
    end
    redis.call('HSET', KEYS[1], 'result', a.resultReference)
    return 'true'
end
local item = read(a.ordinal)
if op == 'claim' then
    deadline()
    if item.status == 'Running' and item.owner == a.owner and item.token == a.token and item.leaseUntil > now then
        return cjson.encode(item)
    end
    if item.status ~= 'Pending' and item.status ~= 'Retrying' then return 'null' end
    if item.status == 'Retrying' and item.notBefore > now then return 'null' end
    if running >= d.maxConcurrency then return 'null' end
    if item.attempt >= 100 then stop('ATTEMPT_LIMIT') end
    local previous = item.status
    item.status, item.owner, item.token = 'Running', a.owner, a.token
    item.attempt, item.fence = item.attempt + 1, nextFence()
    item.leaseUntil = math.min(now + a.duration, d.deadline)
    item.callInFlight, item.mayWrite, item.uncertain = false, false, false
    item.checkpointSequence = 0
    item.diagnostic = nil
    save(item)
    redis.call('ZADD', KEYS[3], item.leaseUntil, field(item.ordinal))
    expire(KEYS[3])
    incr('running', 1)
    if previous == 'Pending' then incr('materialized', 1)
    else
        redis.call('ZREM', KEYS[5], field(item.ordinal))
        incr('retrying', -1)
    end
    return cjson.encode(item)
end
if op == 'retry' then
    -- Repeating a retry request must not reset an item claimed by the next worker.
    if item.retryRequest == a.requestId then
        if item.retryAttempt ~= a.expectedAttempt or item.retryNotBefore ~= a.notBefore
            or item.retryAcknowledged ~= a.acknowledgeUncertain then stop('RETRY_CONFLICT') end
        return 'false'
    end
    deadline()
    if item.attempt ~= a.expectedAttempt then stop('ATTEMPT_CONFLICT') end
    if item.status ~= 'Failed' and item.status ~= 'AwaitingReview' then stop('NOT_RETRYABLE') end
    if item.uncertain and not a.acknowledgeUncertain then stop('ACKNOWLEDGEMENT_REQUIRED') end
    if item.attempt >= 100 then stop('ATTEMPT_LIMIT') end
    if a.notBefore >= d.deadline then stop('DEADLINE') end
    local previous = item.status
    item.status, item.notBefore, item.retryRequest = 'Retrying', a.notBefore, a.requestId
    item.retryAttempt, item.retryNotBefore, item.retryAcknowledged = a.expectedAttempt, a.notBefore, a.acknowledgeUncertain
    save(item)
    redis.call('ZADD', KEYS[5], a.notBefore, field(item.ordinal))
    expire(KEYS[5])
    incr('retrying', 1)
    incr(previous == 'Failed' and 'failed' or 'review', -1)
    return 'true'
end
if op == 'complete' and item.status == 'Completed' then
    if owns(item) and item.resultReference == a.resultReference then return 'false' end
    stop('COMPLETION_CONFLICT')
end
if op == 'fail' and (item.status == 'Failed' or item.status == 'AwaitingReview') then
    if owns(item) and item.failureNeedsReview == a.needsReview and item.diagnostic == a.diagnostic then return 'false' end
    stop('FAILURE_CONFLICT')
end
active(item)
if op == 'renew' then
    item.leaseUntil = math.min(now + a.duration, d.deadline)
    save(item)
    redis.call('ZADD', KEYS[3], item.leaseUntil, field(item.ordinal))
    return 'true'
end
if op == 'checkpoint' then
    if a.checkpointSequence == (item.checkpointSequence or 0)
        and item.checkpointReference == a.checkpointReference
        and item.callInFlight == a.callInFlight and item.mayWrite == a.mayWrite then return 'false' end
    if a.checkpointSequence ~= (item.checkpointSequence or 0) + 1 then stop('CHECKPOINT_CONFLICT') end
    item.checkpointSequence = a.checkpointSequence
    item.checkpointReference, item.callInFlight, item.mayWrite = a.checkpointReference, a.callInFlight, a.mayWrite
    save(item)
    return 'true'
end
if op == 'complete' then
    item.status, item.resultReference = 'Completed', a.resultReference
    item.callInFlight, item.mayWrite, item.uncertain = false, false, false
    save(item)
    redis.call('HSET', KEYS[4], field(item.ordinal), a.resultReference)
    expire(KEYS[4])
    release(item)
    incr('completed', 1)
    return 'true'
end
if op == 'fail' then
    item.uncertain = item.callInFlight == true and item.mayWrite == true
    item.status = (a.needsReview or item.uncertain) and 'AwaitingReview' or 'Failed'
    item.diagnostic = a.diagnostic
    item.failureNeedsReview = a.needsReview
    save(item)
    release(item)
    incr(item.status == 'Failed' and 'failed' or 'review', 1)
    return 'true'
end
stop('UNKNOWN_OPERATION')
