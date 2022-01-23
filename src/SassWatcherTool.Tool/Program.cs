using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SassWatcherTool.Tool;
using Serilog;
using Serilog.Core;
using Serilog.Events;

LoggingLevelSwitch allSwitch = new( LogEventLevel.Warning );
LoggingLevelSwitch restSwitch = new( LogEventLevel.Information );

try
{
    Log.Logger = new LoggerConfiguration()
        .WriteTo.Console()
        .CreateLogger();

    var rootCommand = new RootCommand();

    var verboseLoggingOption = new Option<bool>( "-v", "Verbose logging" );
    var systemVerboseLoggingOption = new Option<bool>( "--vv", "Verbose + host logging" );

    rootCommand.AddGlobalOption( verboseLoggingOption );
    rootCommand.AddGlobalOption( systemVerboseLoggingOption );
    rootCommand.AddCommand( new WatchCommand() );
    rootCommand.Name = "sass-watcher-tool";

    var builder = new CommandLineBuilder( rootCommand );
    Parser host = builder
       .UseDefaults()
       .UseHost( _ => Host.CreateDefaultBuilder( args ), builder => {
           builder
            .UseContentRoot( System.IO.Directory.GetCurrentDirectory() )
            .ConfigureHostConfiguration( configBuilder => {
                configBuilder
                    .AddInMemoryCollection( new Dictionary<string, string> { { "Environment", "Developement" } } )
                    .AddEnvironmentVariables( "DOTNET_" );
            } )
            .ConfigureServices( services => {
                ServiceProvider? scope = services.BuildServiceProvider();
                ParseResult? parseResult = scope.GetRequiredService<ParseResult>();
                var isVerboseLogging = parseResult.GetValueForOption<bool>( verboseLoggingOption );
                var issyStemVerboseLogging = parseResult.GetValueForOption<bool>( systemVerboseLoggingOption );

                if ( isVerboseLogging )
                {
                    restSwitch.MinimumLevel = LogEventLevel.Verbose;
                }
                if ( issyStemVerboseLogging )
                {
                    restSwitch.MinimumLevel = LogEventLevel.Verbose;
                    allSwitch.MinimumLevel = LogEventLevel.Verbose;
                }
            } )
            .ConfigureLogging( ( ctx, builder ) => {
                LoggerConfiguration loggerConfiguration = CreateSerilogLogger( ctx.Configuration, "ZipFiles" );

                Log.Logger = loggerConfiguration.CreateLogger();

                builder.ClearProviders();
                builder.AddSerilog( Log.Logger, true );
            } )
            .UseCommandHandler<WatchCommand, WatchCommand.Handler>();
       } )
       .Build();

    //await rootCommand.InvokeAsync( @"watch D:\Dev\Experiments\BlazorFromZero" );
    await host.InvokeAsync( args );
}
catch ( Exception ex )
{
    Log.Logger.Error( ex, "Unhandled error" );
}

LoggerConfiguration CreateSerilogLogger( IConfiguration configuration, string appName )
{
    LoggerConfiguration loggerConfig = new LoggerConfiguration()
        .MinimumLevel.ControlledBy( restSwitch )
        .MinimumLevel.Override( "Microsoft", allSwitch )
        .Enrich.WithProperty( "ApplicationContext", appName )
        .Enrich.FromLogContext()
        .WriteTo.Async( inner => {
            inner.Console(
                outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}",
                theme: Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme.Code );
        }, blockWhenFull: true )
        .ReadFrom.Configuration( configuration );

    return loggerConfig;
}