namespace SassWatcherTool.Tool;

using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using DartSassHost;
using JavaScriptEngineSwitcher.V8;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class WatchCommand : Command
{
    public WatchCommand()
        : base( "watch", "Watch Sass files" )
    {
        var directoryArg = new Argument( "dir", "Directory" );
        directoryArg.SetDefaultValue( new DirectoryInfo( Directory.GetCurrentDirectory() ) );

        var includeOption = new Option<List<string>>( "--include-globs", () => new List<string> { "**/*.sass", "**/*.scss" } );
        var exludeOption = new Option<List<string>>( "--exclude-globs", () => new List<string> { "**/_*.sass", "**/_*.scss", "**/.vs/**", "**/bin/", "**/obj/" } );

        AddArgument( directoryArg );
        AddOption( includeOption );
        AddOption( exludeOption );
    }

    public new class Handler : ICommandHandler
    {
        private const string ConfigFilename = "sass-watcher-tool.json";
        private Matcher _matcher = null!;

        private readonly ILogger<WatchCommand> _logger;
        private readonly IHostEnvironment _env;
        private readonly Lazy<SassCompiler> _compiler = new( () => new SassCompiler( new V8JsEngineFactory() ) );

        public Handler( ILogger<WatchCommand> logger, IHostEnvironment env )
        {
            _logger = logger;
            _env = env;
        }

        public DirectoryInfo Dir { get; set; } = null!;
        public List<string> IncludeGlobs { get; set; } = new List<string>();
        public List<string> ExcludeGlobs { get; set; } = new List<string>();
        public bool Compressed { get; set; }
        private string? ConfigFilePath { get; set; }
        private List<SourceTargetOption> Sources { get; set; } = new List<SourceTargetOption>();

        public async Task<int> InvokeAsync( InvocationContext context )
        {
            ConfigFilePath = GetConfigFile( Dir.FullName );
            if ( ConfigFilePath is not null )
            {
                _logger.LogInformation( "Reading settings from '{File}'", ConfigFilePath );
                var configFileContent = File.ReadAllText( ConfigFilePath );
                OptionsFile? optionsFile = JsonSerializer.Deserialize<OptionsFile>( configFileContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true } );
                if ( optionsFile is not null )
                {
                    optionsFile.IncludeGlobs.ForEach( IncludeGlobs.Add );
                    optionsFile.ExcludeGlobs.ForEach( ExcludeGlobs.Add );
                    Compressed = optionsFile.Compressed;

                    foreach ( OptionsFile.SourceTargetOptionsFile sourceInfo in optionsFile.Sources )
                    {
                        string sourceFileFullPath = new FileInfo( Path.Combine( Dir.FullName, sourceInfo.Source ) ).FullName;
                        string targetFileFullPath = Path.Combine( Dir.FullName, sourceInfo.Target );
                        var targetFileName = Path.GetFileName( targetFileFullPath );

                        if ( !string.IsNullOrEmpty( targetFileName ) )
                        {
                            targetFileFullPath = new FileInfo( targetFileFullPath ).FullName;
                        }
                        else
                        {
                            var sourceFileName = Path.GetFileNameWithoutExtension( sourceFileFullPath );
                            targetFileFullPath = new FileInfo( Path.Combine( targetFileFullPath, $"{sourceFileName}.css" ) ).FullName;
                        }

                        Sources.Add( new SourceTargetOption( sourceFileFullPath, targetFileFullPath ) );
                    }
                }
            }

            IncludeGlobs = IncludeGlobs.Distinct().ToList();
            ExcludeGlobs = ExcludeGlobs.Distinct().ToList();

            _matcher = CreateGlobMatcher();

            CompilationOptions compilationOptions = new() {
                SourceMap = true,
                InlineSourceMap = true,
                OutputStyle = Compressed ? OutputStyle.Compressed : OutputStyle.Expanded
            };

            string relativPath = Path.GetRelativePath( _env.ContentRootPath, Dir.FullName );
            List<SassFile> sassFiles = ScanDirectory( relativPath ).ToList();

            foreach ( SassFile sassFile in sassFiles )
            {
                Compile( sassFile, compilationOptions );
            }

            Channel<FileSystemEventArgs> changeStream = Channel.CreateUnbounded<FileSystemEventArgs>();

            using FileSystemWatcher watcher = new();
            watcher.Filters.Add( "*.sass" );
            watcher.Filters.Add( "*.scss" );
            watcher.Path = Dir.FullName!;
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
            watcher.Changed += async ( sender, e ) => {
                await changeStream.Writer.WriteAsync( e );
            };
            watcher.Created += async ( sender, e ) => {
                await changeStream.Writer.WriteAsync( e );
            };
            watcher.Deleted += async ( sender, e ) => {
                await changeStream.Writer.WriteAsync( e );
            };
            watcher.Renamed += async ( sender, e ) => {
                var dir = Path.GetDirectoryName( e.OldFullPath );
                var ev = new FileSystemEventArgs( WatcherChangeTypes.Renamed, Dir.FullName, e.OldName );
                await changeStream.Writer.WriteAsync( ev );
            };
            watcher.EnableRaisingEvents = true;

            context.GetCancellationToken().Register( () => {
                watcher.EnableRaisingEvents = false;
                changeStream.Writer.Complete();
            } );

            try
            {
                _logger.LogInformation( "Started watched '{Folder}'", Dir.FullName );

                using MemoryCache cache = new( Microsoft.Extensions.Options.Options.Create( new MemoryCacheOptions() ) );
                await foreach ( FileSystemEventArgs fileEvent in changeStream.Reader.ReadAllAsync() )
                {
                    await Task.Delay( 100 );

                    var cachedPath = cache.Get( fileEvent.FullPath );
                    if ( cachedPath is not null )
                    {
                        _logger.LogTrace( "Skipping duplicate event for {File}", fileEvent.FullPath );
                        continue;
                    }
                    cache.Set( fileEvent.FullPath, fileEvent.FullPath, TimeSpan.FromMilliseconds( 200 ) );

                    var triggerFileName = Path.GetFileName( fileEvent.FullPath );
                    SassFile? matchedSassFile = sassFiles.SingleOrDefault( f => string.Equals( f.Path, fileEvent.FullPath, StringComparison.OrdinalIgnoreCase ) );

                    var triggeredRelativePath = GetRelativePath( fileEvent.FullPath );
                    if ( fileEvent.ChangeType == WatcherChangeTypes.Changed )
                    {
                        _logger.LogInformation( "Changed -> {File}", triggeredRelativePath );
                    }
                    if ( fileEvent.ChangeType == WatcherChangeTypes.Created )
                    {
                        _logger.LogInformation( "Created -> {File}", triggeredRelativePath );
                    }
                    if ( fileEvent.ChangeType == WatcherChangeTypes.Renamed )
                    {
                        _logger.LogInformation( "Renamed -> {File}", triggeredRelativePath );
                    }
                    if ( fileEvent.ChangeType == WatcherChangeTypes.Deleted )
                    {
                        _logger.LogInformation( "Deleted -> {File}", triggeredRelativePath );
                    }

                    List<SassFile> filesToCompile = new();

                    if ( matchedSassFile is not null )
                    {
                        filesToCompile.Add( matchedSassFile );
                    }
                    else if ( !triggerFileName.StartsWith( "_", StringComparison.OrdinalIgnoreCase ) )
                    {
                        // New file
                        var newSassFile = new SassFile( fileEvent.FullPath );
                        sassFiles.Add( newSassFile );
                        filesToCompile.Add( newSassFile );
                    }
                    else
                    {
                        foreach ( SassFile sassFile in sassFiles )
                        {
                            if ( sassFile.PartialFiles.Any( p => string.Equals( p.Path, fileEvent.FullPath, StringComparison.OrdinalIgnoreCase ) ) )
                            {
                                filesToCompile.Add( sassFile );
                            }
                        }
                    }

                    var filesToForget = new List<SassFile>();
                    foreach ( SassFile sassFile in filesToCompile )
                    {
                        if ( !File.Exists( sassFile.Path ) )
                        {
                            filesToForget.Add( sassFile );
                            continue;
                        }

                        Compile( sassFile, compilationOptions );

                        sassFile.LastCompiled = DateTimeOffset.Now;
                    }

                    filesToForget.ForEach( f => sassFiles.Remove( f ) );
                }
            }
            catch ( Exception ex )
            {
                _logger.LogCritical( ex, "Error in file watcher" );
            }

            return 0;
        }

        private IEnumerable<SassFile> ScanDirectory( string rootDirectory )
        {
            IEnumerable<string>? results = _matcher.GetResultsInFullPath( rootDirectory );

            return results.Select( r => new SassFile( r ) );
        }

        private static string? GetConfigFile( string folder )
        {
            var path = Path.Combine( folder, ConfigFilename );
            if ( File.Exists( path ) )
            {
                return path;
            }

            return null;
        }

        private Matcher CreateGlobMatcher()
        {
            var matcher = new Matcher( StringComparison.OrdinalIgnoreCase );
            IncludeGlobs.ForEach( s => matcher.AddInclude( s ) );
            ExcludeGlobs.ForEach( s => matcher.AddExclude( s ) );

            return matcher;
        }

        private bool Compile( SassFile sassFile, CompilationOptions compilationOptions )
        {
            SourceTargetOption? sourceTargetOption = null;
            if ( ConfigFilePath is not null )
            {
                foreach ( SourceTargetOption sourceInfo in Sources )
                {
                    if ( string.Equals( sourceInfo.Source, sassFile.Path, StringComparison.OrdinalIgnoreCase ) )
                    {
                        sourceTargetOption = sourceInfo;
                        break;
                    }
                }
            }

            CompilationResult? compileResult;
            try
            {
                _logger.LogInformation( "\t{File}", GetRelativePath( sassFile.Path ) );
                compileResult = _compiler.Value.CompileFile( sassFile.Path, options: compilationOptions );
                if ( _logger.IsEnabled( LogLevel.Trace ) )
                {
                    foreach ( var file in compileResult.IncludedFilePaths )
                    {
                        if ( string.Equals( sassFile.Path, file, StringComparison.OrdinalIgnoreCase ) )
                        {
                            continue;
                        }
                        _logger.LogTrace( "\t- {File}", GetRelativePath( file ) );
                    }
                }
            }
            catch ( SassCompilationException ex )
            {
                if ( !string.IsNullOrWhiteSpace( ex.File ) )
                {
                    if ( !string.Equals( ex.File, sassFile.Path, StringComparison.OrdinalIgnoreCase ) )
                    {
                        if ( !sassFile.PartialFiles.Any( pf => string.Equals( pf.Path, ex.File, StringComparison.OrdinalIgnoreCase ) ) )
                        {
                            sassFile.PartialFiles.Add( new SassPartialFile( ex.File ) );
                        }
                    }
                }
                LogCompilationError( ex );
                return false;
            }
            catch ( Exception e )
            {
                LogUnhandledException( e );
                return false;
            }

            try
            {
                string targetFile = Path.Combine( Path.GetDirectoryName( sassFile.Path ) ?? string.Empty, $"{Path.GetFileNameWithoutExtension( sassFile.Path )}.css" );
                if ( sourceTargetOption is not null )
                {
                    targetFile = sourceTargetOption.Target;
                }

                _logger.LogInformation( "\t\t-> {File}", GetRelativePath( targetFile ) );
                File.WriteAllText( targetFile, compileResult.CompiledContent );
            }
            catch ( Exception ex )
            {
                _logger.LogError( "Could not write file '{File}': {Message}", GetRelativePath( sassFile.Path ), ex.Message );
                return false;
            }

            var sassFileRelativePath = GetRelativePath( sassFile.Path );
            sassFile.PartialFiles.Clear();
            foreach ( var file in compileResult.IncludedFilePaths )
            {
                var includedFileRelativePath = GetRelativePath( file );
                if ( string.Equals( includedFileRelativePath, sassFileRelativePath, StringComparison.OrdinalIgnoreCase ) )
                {
                    continue;
                }

                sassFile.PartialFiles.Add( new SassPartialFile( file ) );
            }

            return true;
        }

        private string GetRelativePath( string path )
            => Path.GetRelativePath( Dir.FullName, path );

        private void LogCompilationError( SassCompilationException ex )
        {
            var relativePath = Path.GetRelativePath( Dir.FullName, ex.File );
            _logger.LogCritical( "Compilation error\n\tFile: {File}\n\tMessage: {Message}\n{Fragment}", relativePath, ex.Description, ex.SourceFragment );
        }

        private void LogUnhandledException( Exception ex )
        {
            _logger.LogCritical( ex, "Unhandled Exception" );
        }

        internal class OptionsFile
        {
            public List<string> IncludeGlobs { get; set; } = new List<string>();
            public List<string> ExcludeGlobs { get; set; } = new List<string>();
            public bool Compressed { get; set; }
            public List<SourceTargetOptionsFile> Sources { get; set; } = new List<SourceTargetOptionsFile>();

            internal record SourceTargetOptionsFile( string Source, string Target );
        }

        internal record SourceTargetOption( string Source, string Target );

        internal class SassFile
        {
            public SassFile( string path ) => Path = path;

            public string Path { get; private set; }
            public DateTimeOffset? LastCompiled { get; set; }
            public List<SassPartialFile> PartialFiles { get; set; } = new List<SassPartialFile>();

        }
        internal record SassPartialFile
        {
            public string Path { get; set; }
            public SassPartialFile( string path ) => Path = path;
        };
    }
}
