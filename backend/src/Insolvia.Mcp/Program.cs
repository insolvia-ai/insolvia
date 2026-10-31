using Insolvia.Mcp.Hosting;

var builder = WebApplication.CreateBuilder(args);

// One binary, two hosts. Inside Lambda (AWS_LAMBDA_FUNCTION_NAME is set) this
// swaps Kestrel for the Lambda runtime client and translates HTTP API
// payload-v2 events (infra/modules/mcp_service's shape) into ASP.NET Core
// requests. Everywhere else it is a no-op and Kestrel serves: `dotnet run`
// (http://127.0.0.1:8788, the development port kept from the Python service), the image's
// mcp-development stage, and WebApplicationFactory.
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);

// Configuration, logging, the ports and the MCP server;
// Hosting/McpComposition.cs says what differs between the Lambda and a
// developer's machine.
builder.AddInsolviaMcp();

var app = builder.Build();

app.UseInsolviaMcp();

app.Run();
