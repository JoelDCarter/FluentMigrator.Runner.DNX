﻿using DocoptNet;
using FluentMigrator.Runner.Announcers;
using FluentMigrator.Runner.Initialization;
using Microsoft.Extensions.PlatformAbstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using static System.Console;

namespace FluentMigrator.Runner.DNX
{
    public class Program
    {
        private readonly IApplicationEnvironment appEnvironment;

        public Program(IApplicationEnvironment appEnvironment)
        {
            this.appEnvironment = appEnvironment;
        }

        public void Main(string[] args)
        {
            const string usage = @"Fluent Migrator DNX Runner
  Usage:
    dnx run --provider PROVIDER --connectionString CONNECTION [--output FILE] [--assembly ASSEMBLY] [--task TASK] [--migrateToVersion END] [--profile PROFILE] [--tag TAG] [--context VALUE] [--verbose]
    dnx run --provider PROVIDER --noConnection --output FILE [--assembly ASSEMBLY] [--task TASK] [--startVersion START] [--migrateToVersion END] [--profile PROFILE] [--tag TAG] [--context VALUE] [--verbose]
    dnx run --version
    dnx run --help

  Options:
    --provider PROVIDER -p PROVIDER                 Database type. Possible values:
                                                       * sqlserver2000
                                                       * sqlserver2005
                                                       * sqlserver2008
                                                       * sqlserver2012
                                                       * sqlserverce
                                                       * sqlserver
                                                       * mysql
                                                       * postgres
                                                       * oracle
                                                       * sqlite
                                                       * jet
    --connectionString CONNECTION -c CONNECTION           The connection string. Required.
    --assembly ASSEMBLY -a ASSEMBLY                 Optional. The project or assembly which contains the migrations
                                                    You may use a dll path or a path do DNX project.
                                                    It will default to the current path.
    --output FILE -o FILE                           File to output the script. If specified will write to a file instead of running the migration. [default: migration.sql]
    --task TASK -t TASK                             The task to run. [default: migrate]
    --noConnection                                  Indicates that migrations will be generated without consulting a target database. Should only be used when generating an output file.
    --startVersion START                            The specific version to start migrating from. Only used when NoConnection is true. [default: 0]
    --migrateToVersion END                          The specific version to migrate. Default is 0, which will run all migrations. [default: 0]
    --profile PROFILE                               The profile to run after executing migrations.
    --tag TAG                                       Filters the migrations to be run by tag.
    --context VALUE                                 A string argument that can be used in a migration.
    --verbose                                       Verbose. Optional.
    --help -h                                       Show this screen.
    --version -v                                    Show version.
";

            var argsWithRun = new[] { "run" }.Union(args).ToArray();
            var arguments = new Docopt().Apply(usage, argsWithRun, version: Assembly.GetExecutingAssembly().GetName().Version, exit: true);
            verbose = arguments["--verbose"].IsTrue;
            provider = arguments["--provider"].ToString();
            noConnection = arguments["--noConnection"].IsTrue;
            if (noConnection)
                startVersion = arguments["--startVersion"].AsLong();
            else
                connection = arguments["--connectionString"].ToString();
            assembly = (arguments["--assembly"] != null)
                ? assembly = arguments["--assembly"].ToString()
                : appEnvironment.ApplicationBasePath;
            if (!Path.IsPathRooted(assembly))
                assembly = Path.GetFullPath(Path.Combine(appEnvironment.ApplicationBasePath, assembly));
            if (string.Compare(Path.GetExtension(assembly), ".dll", StringComparison.OrdinalIgnoreCase) == 0)
            {
                if (!File.Exists(assembly))
                {
                    WriteLine($"File {assembly} does not exist.");
                    return;
                }
            }
            else
            {
                if (assembly.Substring(assembly.Length - 1)[0] == Path.DirectorySeparatorChar)
                    assembly = assembly.Substring(0, assembly.Length - 1);
                if (!Directory.Exists(assembly))
                {
                    WriteLine($"Directory {assembly} does not exist.");
                    return;
                }
                assembly = DnuBuild();
                if (assembly == null) return;
            }
            if (arguments["--task"] != null)
                task = arguments["--task"].ToString();
            if (arguments["--migrateToVersion"] != null)
                migrateToVersion = arguments["--migrateToVersion"].AsLong();
            if (arguments["--profile"] != null)
                profile = arguments["--profile"].ToString();
            if (arguments["--tag"] != null)
                tags = arguments["--tag"].AsList.Cast<string>();
            if (arguments["--context"] != null)
                applicationContext = arguments["--context"].ToString();
            if (arguments["--output"].ToString() == "migration.sql")
                ExecuteMigrations();
            else
                ExecuteMigrations(arguments["--output"].ToString());
        }

        private string DnuBuild()
        {
            var dnuPath = Environment.GetEnvironmentVariable("PATH").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(p => File.Exists(Path.Combine(p, "dnu.cmd")));
            if (dnuPath == null)
            {
                WriteLine("Dnu not found.");
                return null;
            }
            var dnuCmd = Path.Combine(dnuPath, "dnu.cmd");
            if (verbose) WriteLine($"Found dnu: {dnuCmd}");
            var cmd = Environment.GetEnvironmentVariable("ComSpec");
            if (verbose) WriteLine($"Building project directory: {assembly}");
            var processStartInfo = new ProcessStartInfo(cmd, $@"/c ""{dnuCmd}"" build --quiet --configuration Debug --framework dnx451")
            {
                WorkingDirectory = assembly,
                UseShellExecute = false
            };
            if (!verbose) processStartInfo.RedirectStandardOutput = true;
            using (var process = new Process { StartInfo = processStartInfo })
            {
                process.Start();
                process.WaitForExit();
            }
            var migrationsDllPath = Path.Combine(assembly, @"bin\Debug\dnx451", Path.GetFileName(assembly) + ".dll");
            if (!File.Exists(migrationsDllPath))
            {
                WriteLine($"Could not find assembly ${migrationsDllPath}.");
                return null;
            }
            return migrationsDllPath;
        }

        private readonly ConsoleAnnouncer consoleAnnouncer = new ConsoleAnnouncer();
        private bool verbose;
        private bool noConnection;
        private string provider;
        private string connection;
        private string task;
        private long startVersion;
        private long migrateToVersion;
        private string profile;
        private IEnumerable<string> tags;
        private string assembly;
        private string applicationContext;

        private void ExecuteMigrations()
        {
            consoleAnnouncer.ShowElapsedTime = verbose;
            consoleAnnouncer.ShowSql = verbose;
            ExecuteMigrations(consoleAnnouncer);
        }

        private void ExecuteMigrations(string outputTo)
        {
            using (var sw = new StreamWriter(outputTo))
            {
                var fileAnnouncer = ExecutingAgainstMsSql ?
                    new TextWriterWithGoAnnouncer(sw) :
                    new TextWriterAnnouncer(sw);
                fileAnnouncer.ShowElapsedTime = false;
                fileAnnouncer.ShowSql = true;
                consoleAnnouncer.ShowElapsedTime = verbose;
                consoleAnnouncer.ShowSql = verbose;
                var announcer = new CompositeAnnouncer(consoleAnnouncer, fileAnnouncer);
                ExecuteMigrations(announcer);
            }
        }

        private bool ExecutingAgainstMsSql =>
            provider.StartsWith("SqlServer", StringComparison.InvariantCultureIgnoreCase);

        private void ExecuteMigrations(IAnnouncer announcer) =>
            new TaskExecutor(new RunnerContext(announcer)
            {
                Database = provider,
                Connection = connection,
                Targets = new[] { assembly },
                PreviewOnly = false,
                Task = task,
                NoConnection = noConnection,
                StartVersion = startVersion,
                Version = migrateToVersion,
                Profile = profile,
                Tags = tags,
                ApplicationContext = applicationContext
            }).Execute();
    }
}