using Insolvia.Api.Hosting;

var builder = WebApplication.CreateBuilder(args);

// One binary, two hosts. Inside Lambda (AWS_LAMBDA_FUNCTION_NAME is set) this
// swaps Kestrel for the Lambda runtime client and translates HTTP API
// payload-v2 events into ASP.NET Core requests. Everywhere else it is a no-op
// and Kestrel serves: `dotnet run`, docker compose, WebApplicationFactory.
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);

// Configuration, logging and the ports; Hosting/ApiComposition.cs says what
// differs between the Lambda and a developer's machine.
builder.AddInsolviaApi();

var app = builder.Build();

app.UseInsolviaApi();

app.Run();
