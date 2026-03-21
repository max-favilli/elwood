# 36 — RSA Signature

## Expression
```
$.payload.rsaSign($.payload, $.privateKey)
```

## Traditional JSONPath equivalent
```
$.RsaCryptoSignature('{$.payload}', '{$.privateKey}')
```

## Explanation
- `$.payload` — the data to sign: `"order-12345|2026-03-21"`
- `.rsaSign(data, privateKey)` — signs the data with the RSA private key

### How it works
1. Strip PEM headers from the private key
2. Import the PKCS#1 RSA private key
3. Sign the data using **SHA1** hash + **PKCS1** padding
4. Reverse the signature bytes (matches traditional behavior for Centric PLM compatibility)
5. Return as Base64 string

### rsaSign(data, key)
- `data` — the string to sign
- `key` — PEM-encoded RSA private key (PKCS#1 format, `-----BEGIN RSA PRIVATE KEY-----`)

### Important notes
- The signature is **deterministic** — same data + same key always produces the same signature
- The byte reversal is for compatibility with Traditional JSONPath's `.RsaCryptoSignature()` and the systems it integrates with (e.g. Centric PLM)
- The private key in this test is a **test-only 1024-bit key** — production should use 2048+ bits

### Common use case
Used for authenticating API calls to systems that require RSA-signed request payloads. The integration config stores the private key in Azure App Configuration, and the traditional JSONPath expression signs the request body at runtime.
