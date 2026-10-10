using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Emby.GitHubRepoPluginInstall.Tests;

/// <summary>Logger that keeps what it was told, so tests can check warnings and errors.</summary>
public class TestLogger : ILogger
{
    public List<string> Messages { get; } = new List<string>();

    private void Add(string level, string text) => Messages.Add($"{level}: {text}");
    private static string Fmt(string s, object[] a) => a == null || a.Length == 0 ? s : string.Format(s, a);

    public void Info(string message, params object[] paramList)  => Add("Info", Fmt(message, paramList));
    public void Info(ReadOnlyMemory<char> message)               => Add("Info", message.ToString());
    public void Error(string message, params object[] paramList) => Add("Error", Fmt(message, paramList));
    public void Error(ReadOnlyMemory<char> message)              => Add("Error", message.ToString());
    public void Warn(string message, params object[] paramList)  => Add("Warn", Fmt(message, paramList));
    public void Warn(ReadOnlyMemory<char> message)               => Add("Warn", message.ToString());
    public void Debug(string message, params object[] paramList) => Add("Debug", Fmt(message, paramList));
    public void Debug(ReadOnlyMemory<char> message)              => Add("Debug", message.ToString());
    public void Fatal(string message, params object[] paramList) => Add("Fatal", Fmt(message, paramList));
    public void FatalException(string message, Exception exception, params object[] paramList) => Add("Fatal", Fmt(message, paramList) + exception.Message);
    public void ErrorException(string message, Exception exception, params object[] paramList) => Add("Error", Fmt(message, paramList) + exception.Message);
    public void Log(LogSeverity severity, string message, params object[] paramList) => Add(severity.ToString(), Fmt(message, paramList));
    public void Log(LogSeverity severity, ReadOnlyMemory<char> message) => Add(severity.ToString(), message.ToString());
    public void LogMultiline(string message, LogSeverity severity, StringBuilder additionalContent) => Add(severity.ToString(), message);
}

/// <summary>
/// JSON serializer for tests. Honours [DataMember(Name=...)] on properties the way Emby's serializer does,
/// so the GitHub models deserialize from real GitHub JSON.
/// </summary>
public class TestJson : IJsonSerializer
{
    private class DataMemberResolver : DefaultContractResolver
    {
        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            var prop = base.CreateProperty(member, memberSerialization);
            var dm = member.GetCustomAttribute<DataMemberAttribute>();
            if (dm?.Name != null) prop.PropertyName = dm.Name;
            return prop;
        }
    }

    private static JsonSerializerSettings Settings(bool indent = false) => new JsonSerializerSettings
    {
        ContractResolver  = new DataMemberResolver(),
        Formatting        = indent ? Formatting.Indented : Formatting.None,
        NullValueHandling = NullValueHandling.Ignore
    };

    public object DeserializeFromBytes(ReadOnlySpan<byte> bytes, Type type) => JsonConvert.DeserializeObject(Encoding.UTF8.GetString(bytes), type, Settings());
    public T DeserializeFromBytes<T>(ReadOnlySpan<byte> bytes) => (T)DeserializeFromBytes(bytes, typeof(T));
    public T DeserializeFromFile<T>(string file) where T : class => JsonConvert.DeserializeObject<T>(File.ReadAllText(file), Settings());
    public Task<object> DeserializeFromFileAsync(Type type, string file) => Task.FromResult(JsonConvert.DeserializeObject(File.ReadAllText(file), type, Settings()));
    public Task<T> DeserializeFromFileAsync<T>(string file) where T : class => Task.FromResult(DeserializeFromFile<T>(file));
    public object DeserializeFromSpan(ReadOnlySpan<char> text, Type type) => JsonConvert.DeserializeObject(text.ToString(), type, Settings());
    public T DeserializeFromSpan<T>(ReadOnlySpan<char> text) => (T)DeserializeFromSpan(text, typeof(T));

    public object DeserializeFromStream(Stream stream, Type type)
    {
        using (var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true))
            return JsonConvert.DeserializeObject(reader.ReadToEnd(), type, Settings());
    }

    public T DeserializeFromStream<T>(Stream stream) => (T)DeserializeFromStream(stream, typeof(T));
    public Task<object> DeserializeFromStreamAsync(Stream stream, Type type) => Task.FromResult(DeserializeFromStream(stream, type));
    public Task<T> DeserializeFromStreamAsync<T>(Stream stream) => Task.FromResult(DeserializeFromStream<T>(stream));
    public object DeserializeFromString(string text, Type type) => JsonConvert.DeserializeObject(text, type, Settings());
    public T DeserializeFromString<T>(string text) => JsonConvert.DeserializeObject<T>(text, Settings());
    public void DeserializePartialJsonInto(string json, object target) => JsonConvert.PopulateObject(json, target, Settings());

    public void SerializeToFile(object obj, string file) => File.WriteAllText(file, JsonConvert.SerializeObject(obj, Settings()));
    public void SerializeToFile(object obj, string file, JsonSerializerOptions options) => File.WriteAllText(file, JsonConvert.SerializeObject(obj, Settings(options?.Indent == true)));
    public ReadOnlySpan<char> SerializeToSpan(object obj) => JsonConvert.SerializeObject(obj, Settings()).AsSpan();
    public void SerializeToStream(object obj, Stream stream) => SerializeToStream(obj, stream, null);

    public void SerializeToStream(object obj, Stream stream, JsonSerializerOptions options)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(obj, Settings(options?.Indent == true)));
        stream.Write(bytes, 0, bytes.Length);
    }

    public string SerializeToString(object obj) => JsonConvert.SerializeObject(obj, Settings());
    public string SerializeToString(object obj, JsonSerializerOptions options) => JsonConvert.SerializeObject(obj, Settings(options?.Indent == true));
}

/// <summary>Answers HTTP requests from a table instead of the network.</summary>
public class FakeHandler : HttpMessageHandler
{
    public delegate HttpResponseMessage Responder(HttpRequestMessage request);

    private readonly List<(Func<HttpRequestMessage, bool> Match, Responder Respond)> _routes = new();
    public List<string> Requests { get; } = new List<string>();

    public FakeHandler On(string urlContains, Responder respond)
    {
        _routes.Add((r => r.RequestUri.ToString().Contains(urlContains), respond));
        return this;
    }

    public FakeHandler OnJson(string urlContains, string json, HttpStatusCode status = HttpStatusCode.OK, Action<HttpResponseMessage> headers = null)
    {
        return On(urlContains, _ =>
        {
            var resp = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            headers?.Invoke(resp);
            return resp;
        });
    }

    public FakeHandler OnBytes(string urlContains, byte[] bytes)
    {
        return On(urlContains, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri.ToString());
        foreach (var route in Enumerable.Reverse(_routes))
            if (route.Match(request))
                return Task.FromResult(route.Respond(request));

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
    }
}

/// <summary>
/// A real managed DLL loaded from a folder, the way Emby loads a plugin. Windows maps loaded images, which blocks
/// overwriting them but still allows renaming, and that is exactly what the update code has to cope with.
/// </summary>
public sealed class LoadedDll : IDisposable
{
    private readonly System.Runtime.Loader.AssemblyLoadContext _context;

    public string Path { get; }

    public LoadedDll(string folder, string name)
    {
        Path = System.IO.Path.Combine(folder, name);
        File.Copy(typeof(JsonConvert).Assembly.Location, Path);
        _context = new System.Runtime.Loader.AssemblyLoadContext(name, true);
        _context.LoadFromAssemblyPath(Path);
    }

    /// <summary>True when this machine really refuses to overwrite the loaded file.</summary>
    public bool IsLocked()
    {
        try
        {
            using (var probe = new FileStream(Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { }
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public void Dispose()
    {
        _context.Unload();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}

/// <summary>A scratch folder that is removed when the test is done.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "embyplugintests_" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name, string content = "x")
    {
        var p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }
}
