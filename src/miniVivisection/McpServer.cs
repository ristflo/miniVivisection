using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MiniVivisection
{
    /// <summary>
    /// A small MCP server (Streamable HTTP transport, JSON responses, stateless) on
    /// 127.0.0.1, for Claude Code started by the Claude tab: <c>initialize</c>,
    /// <c>tools/list</c>, <c>tools/call</c>, <c>ping</c>. Each tab registers its tools
    /// under its own random URL (<c>/mcp/&lt;key&gt;</c>), which also keeps other local
    /// processes out. Plain sockets rather than HttpListener: no URL reservation needed.
    /// </summary>
    internal sealed class McpServer
    {
        public sealed class Tool
        {
            public Tool(string name, string description, string inputSchema, Func<JsonElement, Task<string>> handler)
            {
                Name = name;
                Description = description;
                InputSchema = JsonDocument.Parse(inputSchema).RootElement.Clone();
                Handler = handler;
            }

            public string Name { get; }

            public string Description { get; }

            public JsonElement InputSchema { get; }

            public Func<JsonElement, Task<string>> Handler { get; }
        }

        private sealed class Endpoint
        {
            public string Name;
            public string Instructions;
            public Tool[] Tools;
        }

        private const int MaxBody = 4 * 1024 * 1024;

        private static McpServer s_instance;
        private readonly TcpListener _listener;
        private readonly Dictionary<string, Endpoint> _endpoints = new Dictionary<string, Endpoint>();

        private McpServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            // On the thread pool: started from the UI thread, the loop would resume there,
            // and every connection would wait for a busy UI.
            _ = Task.Run(AcceptLoop);
        }

        public static McpServer Instance => s_instance ?? (s_instance = new McpServer());

        public int Port { get; }

        /// <summary>Serves <paramref name="tools"/> as MCP server <paramref name="name"/>;
        /// returns its URL.</summary>
        public string Register(string name, string instructions, IEnumerable<Tool> tools)
        {
            var key = Guid.NewGuid().ToString("N");
            lock (_endpoints)
                _endpoints[key] = new Endpoint { Name = name, Instructions = instructions, Tools = tools.ToArray() };
            return string.Format("http://127.0.0.1:{0}/mcp/{1}", Port, key);
        }

        /// <summary>The tools at <paramref name="url"/> (Register's) gone: their editor closed.</summary>
        public void Unregister(string url)
        {
            var key = url?.Substring(url.LastIndexOf('/') + 1);
            if (key == null)
                return;
            lock (_endpoints)
                _endpoints.Remove(key);
        }

        private async Task AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                _ = Task.Run(() => Serve(client));
            }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    while (true)
                    {
                        var request = await ReadRequest(stream);
                        if (request == null)
                            return;
                        var (status, body) = await Handle(request.Value.Method, request.Value.Path, request.Value.Body);
                        await WriteResponse(stream, status, body);
                    }
                }
                catch (IOException)
                {
                    // the client closed the connection
                }
                catch (Exception ex)
                {
                    Log.Error(LogArea.Mcp, "MCP server: {0}", ex.Message);
                }
            }
        }

        private async Task<(int Status, string Body)> Handle(string method, string path, string body)
        {
            const string prefix = "/mcp/";
            Endpoint endpoint = null;
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                lock (_endpoints)
                    _endpoints.TryGetValue(path.Substring(prefix.Length).TrimEnd('/'), out endpoint);
            }
            if (endpoint == null)
                return (404, null);
            if (method != "POST")
                return (405, null); // no server-to-client stream
            using (var document = JsonDocument.Parse(body))
            {
                var message = document.RootElement;
                if (!message.TryGetProperty("id", out var id))
                    return (202, null); // a notification
                var methodName = message.TryGetProperty("method", out var m) ? m.GetString() : "";
                message.TryGetProperty("params", out var parameters);
                object result;
                try
                {
                    result = await Dispatch(endpoint, methodName, parameters);
                }
                catch (MissingMethodException)
                {
                    return (200, Serialize(new Dictionary<string, object>
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id.Clone(),
                        ["error"] = new { code = -32601, message = "Method not found: " + methodName },
                    }));
                }
                return (200, Serialize(new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id.Clone(), ["result"] = result }));
            }
        }

        private static async Task<object> Dispatch(Endpoint endpoint, string method, JsonElement parameters)
        {
            switch (method)
            {
                case "initialize":
                    var version = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out var v) ? v.GetString() : "2025-06-18";
                    return new
                    {
                        protocolVersion = version,
                        capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = endpoint.Name, version = MiniVivisectionPlugin.ProductVersion + "+" + MiniVivisectionPlugin.BuildNumber },
                        instructions = endpoint.Instructions,
                    };
                case "ping":
                    return new { };
                case "tools/list":
                    return new
                    {
                        tools = endpoint.Tools.Select(t => new { name = t.Name, description = t.Description, inputSchema = t.InputSchema }).ToArray(),
                    };
                case "tools/call":
                    var name = parameters.GetProperty("name").GetString();
                    var arguments = parameters.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                        ? a
                        : JsonDocument.Parse("{}").RootElement;
                    var tool = endpoint.Tools.FirstOrDefault(t => t.Name == name);
                    if (tool == null)
                        return ToolResult("Unknown tool: " + name, isError: true);
                    try
                    {
                        return ToolResult(await tool.Handler(arguments), isError: false);
                    }
                    catch (Exception ex)
                    {
                        return ToolResult(ScriptEditorHook.FirstLine(ex), isError: true);
                    }
                default:
                    throw new MissingMethodException(method);
            }
        }

        private static object ToolResult(string text, bool isError)
        {
            return new { content = new[] { new { type = "text", text = text ?? "" } }, isError };
        }

        private static string Serialize(object value) => JsonSerializer.Serialize(value);

        private static async Task<(string Method, string Path, string Body)?> ReadRequest(NetworkStream stream)
        {
            var header = new MemoryStream();
            var one = new byte[1];
            while (true)
            {
                var read = await stream.ReadAsync(one, 0, 1);
                if (read == 0)
                    return null;
                header.WriteByte(one[0]);
                var length = header.Length;
                if (length >= 4)
                {
                    var buffer = header.GetBuffer();
                    if (buffer[length - 4] == '\r' && buffer[length - 3] == '\n' && buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
                        break;
                }
                if (length > 64 * 1024)
                    return null;
            }
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var requestLine = lines[0].Split(' ');
            var contentLength = 0;
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon > 0 && line.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line.Substring(colon + 1).Trim(), out contentLength);
            }
            if (contentLength < 0 || contentLength > MaxBody)
                throw new IOException("request body of " + contentLength + " bytes refused");
            var body = new byte[contentLength];
            var offset = 0;
            while (offset < contentLength)
            {
                var read = await stream.ReadAsync(body, offset, contentLength - offset);
                if (read == 0)
                    return null;
                offset += read;
            }
            return (requestLine[0], requestLine.Length > 1 ? requestLine[1] : "/", Encoding.UTF8.GetString(body));
        }

        private static async Task WriteResponse(NetworkStream stream, int status, string body)
        {
            var bytes = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
            var reason = status == 200 ? "OK" : status == 202 ? "Accepted" : status == 404 ? "Not Found" : "Method Not Allowed";
            var head = new StringBuilder()
                .AppendFormat("HTTP/1.1 {0} {1}\r\n", status, reason)
                .Append("Content-Type: application/json; charset=utf-8\r\n")
                .AppendFormat("Content-Length: {0}\r\n", bytes.Length)
                .Append(status == 405 ? "Allow: POST\r\n" : "")
                .Append("Connection: keep-alive\r\n\r\n")
                .ToString();
            var headBytes = Encoding.ASCII.GetBytes(head);
            await stream.WriteAsync(headBytes, 0, headBytes.Length);
            if (bytes.Length > 0)
                await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }
    }
}
