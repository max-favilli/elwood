extern alias AzureIdentity;
using System.Text.Json.Nodes;
using Azure.Storage.Blobs;

namespace Elwood.Pipeline.Azure.Tests;

public sealed class RetentionPolicyTests
{
    [M6PolicyFact, Trait("Category", "Integration")]
    public async Task ActualAzurePolicyAndVersionDeletionPermissionsPass()
    {
        var blobUrl = Environment.GetEnvironmentVariable("ELWOOD_M6_BLOB_URL")!;
        _ = M6StorageServiceTests.Container(); // Guard the approved synthetic account.
        var readiness = new AzureRetentionReadiness(new AzureIdentity::Azure.Identity.AzureCliCredential(),
            Environment.GetEnvironmentVariable("ELWOOD_M6_STORAGE_RESOURCE_ID")!,
            new BlobServiceClient(new Uri(blobUrl), new AzureIdentity::Azure.Identity.AzureCliCredential()),
            "contract-state", "contract-documents", "contract-outputs", "preview-catalog", "policytest");
        await readiness.CheckAsync();
        Assert.NotNull(readiness.LastSuccessAt); Assert.Null(readiness.Error);
    }
    private static JsonNode Policy() => JsonNode.Parse("""
        {"rules":[
          {"enabled":true,"definition":{"filters":{"blobTypes":["blockBlob"],"prefixMatch":["states/","documents/","outputs/"]},"actions":{
            "baseBlob":{"delete":{"daysAfterModificationGreaterThan":380}},
            "version":{"delete":{"daysAfterCreationGreaterThan":380}},"snapshot":{"delete":{"daysAfterCreationGreaterThan":380}}}}},
          {"enabled":true,"definition":{"filters":{"blobTypes":["blockBlob"],"prefixMatch":["control/"]},"actions":{
            "version":{"delete":{"daysAfterCreationGreaterThan":7}},"snapshot":{"delete":{"daysAfterCreationGreaterThan":7}}}}}
        ]}
        """)!;

    [Fact]
    public void ExactBackstopsPreserveRecoveryWhileMissingOrShorterRulesFailClosed()
    {
        var policy = Policy();
        AzureRetentionReadiness.ValidatePolicy(policy, "states", "documents", "outputs", "control");
        policy["rules"]![0]!["definition"]!["actions"]!["baseBlob"]!["delete"]!["daysAfterModificationGreaterThan"] = 30;
        Assert.Throws<InvalidOperationException>(() => AzureRetentionReadiness.ValidatePolicy(policy, "states", "documents", "outputs", "control"));
        policy = Policy(); policy["rules"]![0]!["enabled"] = false;
        Assert.Throws<InvalidOperationException>(() => AzureRetentionReadiness.ValidatePolicy(policy, "states", "documents", "outputs", "control"));
        policy = Policy();
        policy["rules"]!.AsArray().Add(JsonNode.Parse("""{"enabled":true,"definition":{"filters":{"prefixMatch":["documents/active/"]},"actions":{"baseBlob":{"delete":{"daysAfterModificationGreaterThan":1}}}}} """));
        Assert.Throws<InvalidOperationException>(() => AzureRetentionReadiness.ValidatePolicy(policy, "states", "documents", "outputs", "control"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("control/")]
    [InlineData("control/elwood/")]
    public void OverlappingCurrentControlDeletionFails(string prefix)
    {
        var policy = Policy();
        policy["rules"]!.AsArray().Add(JsonNode.Parse("""{"enabled":true,"definition":{"filters":{"prefixMatch":[]},"actions":{"baseBlob":{"delete":{"daysAfterModificationGreaterThan":1}}}}} """));
        if (prefix.Length > 0) policy["rules"]![2]!["definition"]!["filters"]!["prefixMatch"]!.AsArray().Add(prefix);
        Assert.Throws<InvalidOperationException>(() => AzureRetentionReadiness.ValidatePolicy(policy, "states", "documents", "outputs", "control"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(365)]
    public void UnboundedOrExcessiveSoftDeleteFails(int days)
    {
        var properties = JsonNode.Parse("""{"isVersioningEnabled":true,"deleteRetentionPolicy":{"enabled":true,"days":7},"containerDeleteRetentionPolicy":{"enabled":true,"days":7}}""")!;
        properties["deleteRetentionPolicy"]!["days"] = days;
        Assert.Throws<InvalidOperationException>(() => AzureRetentionReadiness.ValidateService(properties));
    }
}

public sealed class M6PolicyFactAttribute : FactAttribute
{
    public M6PolicyFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ELWOOD_M6_BLOB_URL")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ELWOOD_M6_STORAGE_RESOURCE_ID")))
            Skip = "Opt-in Azure policy verification requires ELWOOD_M6_BLOB_URL and ELWOOD_M6_STORAGE_RESOURCE_ID.";
    }
}
