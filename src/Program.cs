using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using YtmUrlSharp;

var showConsole = args.Contains("--console", StringComparer.OrdinalIgnoreCase);

if (showConsole)
    AllocConsole();

using var loggerFactory = LoggerFactory.Create(builder =>
{
    if (showConsole)
    {
        builder.SetMinimumLevel(LogLevel.Debug);
        builder.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
    }
});

var logger = loggerFactory.CreateLogger("Program");

var exePath = Environment.ProcessPath;
var buildTime = !string.IsNullOrEmpty(exePath)
    ? File.GetLastWriteTime(exePath)
    : DateTime.MinValue;
logger.LogInformation("YtmUrlSharp build: {BuildTime:yyyy-MM-dd HH:mm:ss}", buildTime);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    using var app = new App(loggerFactory);
    app.Run(cts.Token);
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    logger.LogCritical(ex, "Fatal error");
    MessageBox.Show(ex.ToString(), "YTM URL Sharp - Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    Environment.Exit(1);
}

[DllImport("kernel32")]
static extern bool AllocConsole();
