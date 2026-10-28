using Insolvia.Admin.Hosting;

var builder = WebApplication.CreateBuilder(args);

// One binary, two hosts, as the API: inside Lambda (AWS_LAMBDA_FUNCTION_NAME
// is set) the Lambda runtime client replaces Kestrel and HTTP API payload-v2
// events become ASP.NET Core requests; everywhere else Kestrel serves.
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);

// Hosting/AdminComposition.cs says what differs between the Lambda and a
// developer's machine.
builder.AddInsolviaAdmin();

var app = builder.Build();

app.UseInsolviaAdmin();

app.Run();
