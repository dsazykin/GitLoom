using System.IO;
using GitLoom.Server;
using Microsoft.AspNetCore.Builder;

var options = DaemonOptions.Parse(args);

// --local-dev --smoke: start, self-probe, exit 0 (prints nothing on success).
if (options.Smoke)
{
    return await DaemonHost.RunSmokeAsync(options);
}

// Normal daemon run. Build via the shared host configuration so the in-proc test
// tier (WebApplicationFactory<Program>) exercises the same pipeline. app.Run() is
// reached so the test harness can intercept host startup.
var app = DaemonHost.Build(options);
try
{
    app.Run();
}
catch (IOException ex)
{
    // Loopback port already bound → typed failure naming the port (edge row 3).
    throw new DaemonStartupException(options.Port,
        $"GitLoom daemon could not bind loopback port {options.Port} (already in use?).", ex);
}

return 0;

// Exposed so WebApplicationFactory<Program> can host the daemon in-proc (TI-P2-00).
public partial class Program { }
