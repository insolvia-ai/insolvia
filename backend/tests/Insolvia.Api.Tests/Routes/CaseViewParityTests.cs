using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Api.Cases;
using Insolvia.Api.Http;
using Insolvia.Domain.Cases;
using Insolvia.Domain.Clients;

namespace Insolvia.Api.Tests.Routes;

// Temporary: PublicStatusView against CaseViews.PublicStatus, which stays
// until its one caller (PortalViews) is converted; delete both then.
public sealed class CaseViewParityTests
{
    [Fact]
    public void Parity_public_status()
    {
        var status = new CasePublicStatus(13, CaseStatus.Filed);
        var typed = JsonSerializer.SerializeToNode(PublicStatusView.Of(status), WireJson.Options);
        Assert.True(JsonNode.DeepEquals(CaseViews.PublicStatus(status), typed), FlaskJson.Serialize(typed));
    }
}
