using Insolvia.Api;

var builder = WebApplication.CreateBuilder(args);

// One binary, two hosts. Inside Lambda (AWS_LAMBDA_FUNCTION_NAME is set) this
// swaps Kestrel for the Lambda runtime client and translates HTTP API
// payload-v2 events into ASP.NET Core requests. Everywhere else it is a no-op
// and Kestrel serves: `dotnet run`, docker compose, WebApplicationFactory.
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);

// INSOLVIA_ENV is read through configuration (environment variables are one
// of its sources), so a test host can set it with UseSetting.
builder.Services.AddSingleton(services =>
    InsolviaEnvironment.FromVariable(services.GetRequiredService<IConfiguration>()["INSOLVIA_ENV"]));

var app = builder.Build();

// Resolve it once now: a misspelt INSOLVIA_ENV stops the process at start,
// not on the first request.
_ = app.Services.GetRequiredService<InsolviaEnvironment>();

app.MapHealth();

app.Run();
