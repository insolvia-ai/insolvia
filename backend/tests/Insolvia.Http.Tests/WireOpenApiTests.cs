namespace Insolvia.Http.Tests;

/// <summary>The docs' groups are features, named from the route classes.</summary>
public sealed class WireOpenApiTests
{
    [Theory]
    [InlineData("DebtorRoutes", "Debtor")]
    [InlineData("FirmQuestionnaireRoutes", "Firm questionnaire")]
    [InlineData("HealthEndpoint", "Health")]
    [InlineData("MeRoutes", "Me")]
    [InlineData("Routes", "Routes")]
    public void A_route_class_names_its_feature(string type, string feature) =>
        Assert.Equal(feature, WireOpenApi.Feature(type));
}
