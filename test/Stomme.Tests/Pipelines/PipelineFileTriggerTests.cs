#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Services;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.KeyValues.Features;
using Stomme.Engine.KeyValues.Models;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps.Flowchart.Steps;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's Local File Trigger as "File added": a published pipeline watching a folder of the site's pipeline folder is
    /// started once for each file put there - the file its trigger's result - and never for one already there when it was
    /// published, one its names do not match, one in a subfolder it does not look into, one still being written, or one it has
    /// started for already; "changed files too" takes a file written again; a look starts at most so many, oldest written first;
    /// a folder no trigger watches is forgotten. The engine with the module's steps and task, over a folder of its own.
    /// </summary>
    public sealed class PipelineFileTriggerTests : IAsyncLifetime
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-file-trigger-" + Guid.NewGuid().ToString("N"));
        private readonly PipelineTriggerOptions _options = new();
        private readonly MovableClock _clock = new();
        private ServiceProvider _services = default!;

        // The site's folder: the harness has no tenant, so its folder is "default" under the root.
        private string Site => Path.Combine(_root, "default");

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineTriggerOptions>>(Options.Create(_options));
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions { Files = { Root = _root } }));
            services.AddEngine(module =>
            {
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<KeyValueFeature>(_ => { });
            });
            services.AddSingleton(typeof(MemoryStore<>));
            services.AddNotificationHandler<KeepRunInput, Stomme.Engine.Pipelines.Notifications.PipelineStarted>();
            services.AddSingleton<Stomme.Engine.Common.ISystemClock>(_clock); // after the engine's: the one asked for
            services.AddScoped<WatchedFolders>();
            services.AddScoped<WatchedFoldersTask>();
            services.AddNotificationHandler<StartWatchingOnPublish, Stomme.Engine.Pipelines.Runtime.Notifications.PipelineTriggersIndexed>();
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static async Task<string> PublishAsync(IServiceProvider services, string folder, string pattern, bool subfolders = false, bool changed = false)
        {
            var trigger = new FileAdded { Id = "file", Folder = new(folder), Pattern = new(pattern), Subfolders = new(subfolders), Changed = new(changed) };
            trigger.SetCanStartPipeline(true);
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(new Flowchart { Id = "flow", Steps = { trigger } });
            definition.Name = "Files in " + folder;
            Assert.True((await publisher.PublishAsync(definition)).Succeeded);
            return definition.DefinitionId;
        }

        // A file in the site's folder, written that long ago (a file a look must not wait for).
        private void Put(string path, string text, TimeSpan ago)
        {
            var full = Path.Combine(Site, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
            File.SetLastWriteTimeUtc(full, DateTime.UtcNow - ago);
        }

        // The files each run of the pipeline was started with, as "event path size", oldest run first.
        private static async Task<string[]> StartedWithAsync(IServiceProvider services, string definitionId) =>
            (await services.GetRequiredService<IPipelineInstanceStore>().FindManyAsync(new PipelineInstanceFilter { DefinitionId = definitionId }))
                .OrderBy(run => run.CreatedAt)
                .Select(run => JsonNode.Parse(KeepRunInput.Of(run.PipelineState)[FileAdded.FileInputKey].ToString()!)!)
                .Select(file => $"{file["event"]} {file["path"]} {file["size"]}")
                .ToArray();

        [Fact]
        public async Task A_file_put_in_the_folder_starts_a_run_with_it_and_a_file_already_there_or_not_matching_does_not()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var task = services.GetRequiredService<WatchedFoldersTask>();
            Put("inbox/before.csv", "there first", TimeSpan.FromMinutes(5));
            var watching = await PublishAsync(services, "inbox", "*.csv");

            // What was there when it was published is recorded as it is published: it starts nothing, and a file put there at
            // once is new to the first look, however soon after the publish it comes.
            Put("inbox/orders.csv", "a,b", TimeSpan.FromSeconds(30));
            Put("inbox/notes.txt", "not a csv", TimeSpan.FromSeconds(30));
            Put("inbox/archive/old.csv", "in a subfolder", TimeSpan.FromSeconds(30));
            Put("outbox/other.csv", "another folder", TimeSpan.FromSeconds(30));
            Put("inbox/writing.csv", "still being written", TimeSpan.Zero);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(new[] { "add inbox/orders.csv 3" }, await StartedWithAsync(services, watching));

            // The file still being written is taken once it has settled; nothing is started twice.
            File.SetLastWriteTimeUtc(Path.Combine(Site, "inbox", "writing.csv"), DateTime.UtcNow.AddSeconds(-10));
            await task.ExecuteAsync(CancellationToken.None);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(new[] { "add inbox/orders.csv 3", "add inbox/writing.csv 19" }, await StartedWithAsync(services, watching));

            // Written again: no run - it takes new files only.
            Put("inbox/orders.csv", "a,b,c", TimeSpan.FromSeconds(20));
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(2, (await StartedWithAsync(services, watching)).Length);

            // The trigger's result is the file - n8n's fields, path and event, and its name, folder, size and time.
            var run = (await services.GetRequiredService<IPipelineInstanceStore>().FindManyAsync(new PipelineInstanceFilter { DefinitionId = watching })).OrderBy(run => run.CreatedAt).First();
            var file = JsonNode.Parse(KeepRunInput.Of(run.PipelineState)[FileAdded.FileInputKey].ToString()!)!;
            Assert.Equal(("orders.csv", "inbox"), (file["name"]!.GetValue<string>(), file["folder"]!.GetValue<string>()));
            Assert.True(DateTimeOffset.Parse(file["modifiedAt"]!.GetValue<string>()) < DateTimeOffset.UtcNow.AddSeconds(-25));
        }

        [Fact]
        public async Task Changed_files_too_and_subfolders_and_at_most_so_many_a_look_oldest_first()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var task = services.GetRequiredService<WatchedFoldersTask>();
            _options.FileTriggers.MaxRunsPerLook = 2;
            var watching = await PublishAsync(services, "", "", subfolders: true, changed: true); // the whole pipeline folder, not even made yet

            Put("a/b/third.txt", "3", TimeSpan.FromSeconds(10));
            Put("first.txt", "1", TimeSpan.FromSeconds(30));
            Put("a/second.txt", "22", TimeSpan.FromSeconds(20));
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(new[] { "add first.txt 1", "add a/second.txt 2" }, await StartedWithAsync(services, watching));
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal("add a/b/third.txt 1", (await StartedWithAsync(services, watching))[2]);

            Put("first.txt", "1 again", TimeSpan.FromSeconds(5));
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal("change first.txt 7", (await StartedWithAsync(services, watching))[3]);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(4, (await StartedWithAsync(services, watching)).Length);

            // Written again and still being written: the look waits - and the change is not lost by being seen early. The CLOCK
            // moves on, not the file: a file whose time moved again would be a change whatever was recorded of it.
            Put("a/second.txt", "22 and more", TimeSpan.Zero);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(4, (await StartedWithAsync(services, watching)).Length);
            _clock.Offset = TimeSpan.FromSeconds(10);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal("change a/second.txt 11", (await StartedWithAsync(services, watching))[4]);
        }

        [Fact]
        public async Task A_folder_no_trigger_watches_is_forgotten_and_one_watched_is_kept_per_site()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var keyValues = services.GetRequiredService<IKeyValueStore>();
            var watching = await PublishAsync(services, "inbox", "");
            var stale = WatchedFolders.SeenKey(null, "a-hash-no-trigger-has");
            await keyValues.SaveAsync(new SerializedKeyValuePair { Key = stale, SerializedValue = "{}" }, CancellationToken.None);
            var task = services.GetRequiredService<WatchedFoldersTask>();

            await task.ExecuteAsync(CancellationToken.None);

            var kept = (await keyValues.FindManyAsync(new KeyValueFilter { Key = WatchedFolders.SeenKeyPrefix, StartsWith = true }, CancellationToken.None)).Select(pair => pair.Key).ToList();
            Assert.DoesNotContain(stale, kept);
            var watched = Assert.Single(kept);

            // A record lost (or a folder watched before it was recorded at its publish): the next look records, and starts nothing.
            await keyValues.DeleteAsync(watched, CancellationToken.None);
            Put("inbox/while-forgotten.txt", "x", TimeSpan.FromSeconds(30));
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Empty(await StartedWithAsync(services, watching));
            Put("inbox/after.txt", "y", TimeSpan.FromSeconds(30));
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(new[] { "add inbox/after.txt 1" }, await StartedWithAsync(services, watching));
        }

        // The real time, moved on when a test says - the file system's clock is not.
        private sealed class MovableClock : Stomme.Engine.Common.ISystemClock
        {
            public TimeSpan Offset { get; set; }

            public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + Offset;
        }

        [Fact]
        public void The_folder_and_the_names_are_held_to_the_rules_at_the_trigger_and_at_the_save()
        {
            Assert.Null(PipelineFileEvent.FolderProblem("forms/invoices"));
            Assert.Null(PipelineFileEvent.FolderProblem(""));
            Assert.StartsWith("'../outside' cannot be a folder of the site's pipeline folder", PipelineFileEvent.FolderProblem("../outside"));
            Assert.StartsWith("'C:/data' cannot be a folder", PipelineFileEvent.FolderProblem("C:/data"));
            Assert.Null(PipelineFileEvent.PatternProblem("*.csv; report-??.xlsx"));
            Assert.StartsWith("'in/*.csv' is not a pattern of file names", PipelineFileEvent.PatternProblem("in/*.csv"));
            Assert.Equal("*.csv, report-??.xlsx", PipelineFileEvent.NormalizePattern(" *.csv ;report-??.xlsx, *.CSV"));
            Assert.Throws<InvalidOperationException>(() => FileAdded.Stimulus("../outside", "", false, false));
            Assert.Equal(new FileAddedStimulus("forms/invoices", "*.pdf", true, false), FileAdded.Stimulus("/forms\\invoices/", "*.pdf", true, false));

            // Refused at the save, saying why, as a cron that does not parse is.
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            string? Refusal(string folder, string pattern)
            {
                var graph = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "file", Type = PipelineFileEvent.StepType, Inputs = { ["Folder"] = folder, ["Pattern"] = pattern } } } };
                try
                {
                    PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
                    return null;
                }
                catch (ArgumentException ex)
                {
                    return ex.Message;
                }
            }

            Assert.Null(Refusal("inbox", "*.csv"));
            Assert.StartsWith("'../outside' cannot be a folder", Refusal("../outside", "*.csv"));
            Assert.StartsWith("'a/b.csv' is not a pattern", Refusal("inbox", "a/b.csv"));
        }

        [Theory]
        [InlineData("orders.csv", "*.csv", true)]
        [InlineData("ORDERS.CSV", "*.csv", true)]
        [InlineData("orders.csv.bak", "*.csv", false)]
        [InlineData("report-07.xlsx", "*.csv, report-??.xlsx", true)]
        [InlineData("report-7.xlsx", "report-??.xlsx", false)]
        [InlineData("a", "*", true)]
        [InlineData("anything", "", true)]
        [InlineData("abcabd", "*ab?", true)]
        [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab", "*a*a*a*a*a*a*c", false)]
        public void A_name_matches_its_patterns_case_aside(string name, string pattern, bool matches) =>
            Assert.Equal(matches, PipelineFileEvent.Matches(name, PipelineFileEvent.Patterns(pattern)));

        [Fact]
        public void The_sample_is_a_file_the_trigger_would_take_in_its_folder()
        {
            var sample = JsonNode.Parse(PipelineFileEvent.Sample("inbox", "*.csv", new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero)))!;
            Assert.Equal(("inbox/invoice-1001.csv", "invoice-1001.csv", "inbox", "add"),
                (sample["path"]!.GetValue<string>(), sample["name"]!.GetValue<string>(), sample["folder"]!.GetValue<string>(), sample["event"]!.GetValue<string>()));
            Assert.Equal("2026-10-10T12:00:00.0000000Z", sample["modifiedAt"]!.GetValue<string>());
        }
    }
}
