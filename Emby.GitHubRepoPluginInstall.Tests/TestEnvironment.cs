using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using Emby.GitHubRepoPluginInstall.GithubAPI;
using Emby.GitHubRepoPluginInstall.Models;
using Emby.GitHubRepoPluginInstall.Services;
using Emby.GitHubRepoPluginInstall.Storage;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;

namespace Emby.GitHubRepoPluginInstall.Tests;

// ---- Fakes for the Emby services the plugin talks to ---------------------------------------------------------

public static class Fake
{
    public static object DefaultFor(Type t) => t == typeof(void) || !t.IsValueType ? null : Activator.CreateInstance(t);
}

public class FileSystemProxy : DispatchProxy
{
    protected override object Invoke(MethodInfo m, object[] a)
    {
        var path = a.Length > 0 ? a[0] as string : null;
        switch (m.Name)
        {
            case "DirectoryExists": return Directory.Exists(path);
            case "FileExists":      return File.Exists(path);
            case "CreateDirectory": Directory.CreateDirectory(path); return Fake.DefaultFor(m.ReturnType);
            case "OpenRead":        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            case "GetFileStream":   return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            case "GetLastWriteTimeUtc":
                var utc = File.GetLastWriteTimeUtc(path);
                return m.ReturnType == typeof(DateTimeOffset) ? new DateTimeOffset(utc, TimeSpan.Zero) : (object)utc;
            default: return Fake.DefaultFor(m.ReturnType);
        }
    }
}

public class PathsProxy : DispatchProxy
{
    public Dictionary<string, string> Values { get; } = new Dictionary<string, string>();

    protected override object Invoke(MethodInfo m, object[] a)
    {
        var name = m.Name.StartsWith("get_") ? m.Name.Substring(4) : m.Name;
        return Values.TryGetValue(name, out var v) ? v : Fake.DefaultFor(m.ReturnType);
    }
}

public class ActivityProxy : DispatchProxy
{
    public List<ActivityLogEntry> Entries { get; } = new List<ActivityLogEntry>();

    protected override object Invoke(MethodInfo m, object[] a)
    {
        if (m.Name == "Create") Entries.Add((ActivityLogEntry)a[0]);
        return Fake.DefaultFor(m.ReturnType);
    }
}

public class HostProxy : DispatchProxy
{
    public Dictionary<Type, object> Services { get; } = new Dictionary<Type, object>();
    public List<string> Calls { get; } = new List<string>();

    protected override object Invoke(MethodInfo m, object[] a)
    {
        switch (m.Name)
        {
            case "Resolve":
                var type = m.GetGenericArguments()[0];
                return Services.TryGetValue(type, out var s) ? s : throw new InvalidOperationException($"{type.Name} is not registered in the fake host");
            case "TryResolve":
                return Services.TryGetValue(m.GetGenericArguments()[0], out var t) ? t : null;
            case "Restart":
            case "NotifyPendingRestart":
                Calls.Add(m.Name);
                return null;
            case "get_CanSelfRestart":
                return true;
            case "get_Plugins":
                return Array.Empty<IPlugin>();
            default:
                return Fake.DefaultFor(m.ReturnType);
        }
    }
}

// ---- GitHub JSON helpers ------------------------------------------------------------------------------------

public static class Gh
{
    public static string Release(string tag, string assetsJson, bool prerelease = false, string published = "2025-05-01T00:00:00Z") =>
        $@"{{""tag_name"":""{tag}"",""draft"":false,""prerelease"":{prerelease.ToString().ToLowerInvariant()},
            ""published_at"":""{published}"",""target_commitish"":""main"",""body"":""notes for {tag}"",""assets"":{assetsJson}}}";

    public static string Asset(string repo, string fileName, int id = 1, string digest = null) =>
        $@"{{""id"":{id},""name"":""{fileName}"",""url"":""https://api.github.com/repos/alex/{repo}/releases/assets/{id}"",
             ""browser_download_url"":""https://github.com/alex/{repo}/releases/download/x/{fileName}""
             {(digest == null ? "" : $@",""digest"":""{digest}""")}}}";

    public static string Sha256(byte[] bytes) => "sha256:" + BitConverter.ToString(SHA256.HashData(bytes)).Replace("-", "").ToLowerInvariant();
}

// ---- A whole fake server with real folders ------------------------------------------------------------------

/// <summary>
/// Real temp folders for plugins, config and data, with Emby's services faked around them. The options store,
/// token encryption, backup service and state store are the real ones; only GitHub, the activity log and the
/// server's restart calls are faked.
/// </summary>
public sealed class TestEnv : IDisposable
{
    public TempDir Root { get; } = new TempDir();
    public string Plugins { get; }
    public string Config  { get; }
    public string Data    { get; }

    public TestLogger     Logger   { get; } = new TestLogger();
    public TestJson       Json     { get; } = new TestJson();
    public FakeHandler    Handler  { get; } = new FakeHandler();
    public HostProxy      HostState { get; }
    public ActivityProxy  Activity { get; }
    public IApplicationHost Host   { get; }
    public RepoUpdateService Service { get; }

    public TestEnv(string token = "ghp_testtoken")
    {
        Plugins = Path.Combine(Root.Path, "plugins");
        Config  = Path.Combine(Root.Path, "config");
        Data    = Path.Combine(Root.Path, "data");
        Directory.CreateDirectory(Plugins);
        Directory.CreateDirectory(Config);
        Directory.CreateDirectory(Data);

        var paths = DispatchProxy.Create<IApplicationPaths, PathsProxy>();
        var pathValues = ((PathsProxy)(object)paths).Values;
        pathValues["PluginsPath"] = Plugins;
        pathValues["PluginConfigurationsPath"] = Config;
        pathValues["DataPath"] = Data;

        var activity = DispatchProxy.Create<IActivityManager, ActivityProxy>();
        Activity = (ActivityProxy)(object)activity;

        var host = DispatchProxy.Create<IApplicationHost, HostProxy>();
        HostState = (HostProxy)(object)host;
        HostState.Services[typeof(IApplicationPaths)] = paths;
        HostState.Services[typeof(IJsonSerializer)]   = Json;
        HostState.Services[typeof(IFileSystem)]       = DispatchProxy.Create<IFileSystem, FileSystemProxy>();
        HostState.Services[typeof(IActivityManager)]  = activity;
        Host = host;

        Service = new RepoUpdateService(Host, Logger, t => new GitHubApiClient(t, new HttpClient(Handler), Json, Logger));

        var options = NewStore().GetOptions();
        options.GitHubToken = token;
        NewStore().SetOptions(options);
    }

    public SecurePluginOptionsStore NewStore() => new SecurePluginOptionsStore(Host, Logger, GitHubRepPluginInstall.PluginName);

    public RuntimeStateStore NewState() => new RuntimeStateStore(Host, Logger, GitHubRepPluginInstall.PluginName);

    /// <summary>Settings as saved on disk right now.</summary>
    public PluginUIOptions Options => NewStore().GetOptions();

    public ReposToProcess Repo(string name) => Options.Repos.Single(r => r.Repository == name);

    public ReposToProcess AddRepo(string name, bool auto = true, string installed = null, string fileName = null)
    {
        var store   = NewStore();
        var options = store.GetOptions();
        var repo = new ReposToProcess
        {
            Url = $"https://github.com/alex/{name}",
            AutoUpdate = auto,
            LastVersionDownloaded = installed,
            FileName = fileName
        };
        options.Repos.Add(repo);
        store.SetOptions(options);
        return repo;
    }

    public void SetRestartAfterInstall(bool on)
    {
        var store   = NewStore();
        var options = store.GetOptions();
        options.RestartServerAfterInstall = on;
        store.SetOptions(options);
    }

    /// <summary>Makes GitHub report <paramref name="tag"/> as the latest release of the repo, with one DLL asset.</summary>
    public byte[] Publish(string name, string tag, string content = null, bool goodDigest = true, string fileName = null)
    {
        fileName ??= name + ".dll";
        var bytes = System.Text.Encoding.UTF8.GetBytes(content ?? $"{name} build {tag}");
        var digest = goodDigest ? Gh.Sha256(bytes) : "sha256:" + new string('0', 64);

        Handler.OnJson($"/repos/alex/{name}/releases", "[" + Gh.Release(tag, "[" + Gh.Asset(name, fileName, 1, digest) + "]") + "]");
        Handler.OnBytes($"/repos/alex/{name}/releases/assets/1", bytes);
        return bytes;
    }

    public string PluginFile(string fileName) => Path.Combine(Plugins, fileName);

    public void Dispose() => Root.Dispose();
}
