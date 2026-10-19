using System.Text.Json;

// Scripted Cursor agent CLI. The provider appends login, status, or acp after the configured arguments.
// A --config file tells this process what to print so the tests observe the real process, not a fake provider.

var configPath = ConfigPath(args);
if (configPath is null || !File.Exists(configPath))
{
    Console.Error.WriteLine("missing --config");
    return 2;
}

using var configDocument = JsonDocument.Parse(File.ReadAllText(configPath));
var config = configDocument.RootElement;
var verb = args.LastOrDefault(static argument => argument is "login" or "status" or "acp" or "models" or "logout");
var logPath = Text(config, "log");

switch (verb)
{
    case "logout":
        Append(logPath, "logout");
        Console.Error.WriteLine("logout is not a CodeAlta Cursor verb");
        return 2;
    case "status":
        Append(logPath, "status");
        var authenticated = config.TryGetProperty("authenticated", out var authenticatedValue) && authenticatedValue.GetBoolean();
        Console.WriteLine(authenticated ? "Logged in" : "Not logged in");
        return authenticated ? 0 : 1;
    case "login":
        return Login(config, logPath);
    case "models":
        Append(logPath, "models");
        if (config.TryGetProperty("models", out var listed) && listed.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in listed.EnumerateArray())
            {
                if (line.ValueKind == JsonValueKind.String)
                {
                    Console.WriteLine(line.GetString());
                }
            }
        }

        return 0;
    case "acp":
        return Acp(config, logPath);
    default:
        Console.Error.WriteLine("expected login, status, or acp");
        return 2;
}

static int Login(JsonElement config, string? logPath)
{
    var device = string.Equals(Environment.GetEnvironmentVariable("NO_OPEN_BROWSER"), "1", StringComparison.Ordinal);
    var expectDevice = config.TryGetProperty("device", out var deviceValue) && deviceValue.GetBoolean();
    if (device != expectDevice)
    {
        Console.Error.WriteLine(device ? "unexpected NO_OPEN_BROWSER" : "missing NO_OPEN_BROWSER");
        return 3;
    }

    var url = Text(config, "url");
    if (string.IsNullOrWhiteSpace(url))
    {
        Console.Error.WriteLine("missing url");
        return 2;
    }

    Console.WriteLine(url);
    var userCode = Text(config, "userCode");
    if (!string.IsNullOrWhiteSpace(userCode))
    {
        Console.WriteLine("User code: " + userCode);
    }

    Append(logPath, device ? "device-no-browser" : "browser-login");
    return 0;
}

static int Acp(JsonElement config, string? logPath)
{
    Append(logPath, "model " + ModelArgument());
    Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
    var assistant = Text(config, "assistant") ?? "";
    string? sessionId = null;
    while (Console.In.ReadLine() is { } line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        using var document = JsonDocument.Parse(line);
        var message = document.RootElement;
        if (!message.TryGetProperty("method", out var methodProperty))
        {
            continue;
        }

        var method = methodProperty.GetString();
        var id = message.TryGetProperty("id", out var idProperty) ? idProperty.GetRawText() : null;
        Append(logPath, method + " " + (method == "authenticate" ? MethodId(message) : ""));
        switch (method)
        {
            case "initialize":
                Write($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"protocolVersion\":1}}}}");
                break;
            case "authenticate":
                if (!string.Equals(MethodId(message), "cursor_login", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("expected cursor_login");
                    return 4;
                }

                Write($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{}}}}");
                break;
            case "session/new":
                sessionId = "cursor-session";
                Write($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"sessionId\":\"{sessionId}\"}}}}");
                break;
            case "session/load":
                sessionId = RequestedSessionId(message);
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    Console.Error.WriteLine("session/load missing sessionId");
                    return 7;
                }

                // ACP load results omit sessionId. The client already sent it.
                Write($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{}}}}");
                break;
            case "session/prompt":
                LogPrompt(logPath, message);
                if (config.TryGetProperty("traces", out var traces) && traces.ValueKind == JsonValueKind.Array)
                {
                    foreach (var trace in traces.EnumerateArray())
                    {
                        Write(TraceLine(sessionId, trace));
                    }
                }
                else
                {
                    Write($"{{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{{\"sessionId\":\"{sessionId}\",\"update\":{{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{{\"type\":\"text\",\"text\":{JsonString(assistant)}}}}}}}}}");
                }

                Write("{\"jsonrpc\":\"2.0\",\"id\":91,\"method\":\"session/request_permission\",\"params\":{\"sessionId\":\"" + sessionId + "\",\"options\":[{\"optionId\":\"allow-once\",\"name\":\"Allow\",\"kind\":\"allow_once\"}]}}");
                if (!WaitForPermission(logPath))
                {
                    return 5;
                }

                Write($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"stopReason\":\"end_turn\"}}}}");
                return 0;
            default:
                if (id is not null)
                {
                    Write($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{}}}}");
                }

                break;
        }
    }

    Console.Error.WriteLine("acp ended before session/prompt");
    return 6;
}

static bool WaitForPermission(string? logPath)
{
    while (Console.In.ReadLine() is { } line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        using var document = JsonDocument.Parse(line);
        var message = document.RootElement;
        if (message.TryGetProperty("id", out var idProperty) && idProperty.TryGetInt32(out var id) && id == 91)
        {
            Append(logPath, "permission " + line);
            return message.TryGetProperty("result", out _);
        }
    }

    return false;
}

static string ModelArgument()
{
    var args = Environment.GetCommandLineArgs();
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (args[index] == "--model")
        {
            return args[index + 1];
        }
    }

    return "";
}

static void LogPrompt(string? logPath, JsonElement message)
{
    if (!message.TryGetProperty("params", out var parameters)
        || !parameters.TryGetProperty("prompt", out var prompt)
        || prompt.ValueKind != JsonValueKind.Array)
    {
        return;
    }

    Append(logPath, "prompt-session " + (Text(parameters, "sessionId") ?? ""));
    foreach (var block in prompt.EnumerateArray())
    {
        var type = Text(block, "type");
        if (type == "text")
        {
            Append(logPath, "prompt-text " + (Text(block, "text") ?? ""));
        }
        else if (type == "resource" && block.TryGetProperty("resource", out var resource))
        {
            Append(logPath, "prompt-resource " + (Text(resource, "uri") ?? "") + " " + (Text(resource, "text") ?? ""));
        }
    }
}

static string? RequestedSessionId(JsonElement message)
{
    if (message.TryGetProperty("params", out var parameters) && parameters.TryGetProperty("sessionId", out var sessionId))
    {
        return sessionId.GetString();
    }

    return null;
}

static string? MethodId(JsonElement message)
{
    if (message.TryGetProperty("params", out var parameters) && parameters.TryGetProperty("methodId", out var methodId))
    {
        return methodId.GetString();
    }

    return null;
}

static void Write(string line)
{
    Console.WriteLine(line);
}

static string? ConfigPath(string[] args)
{
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (args[index] == "--config")
        {
            return args[index + 1];
        }
    }

    return null;
}

static string? Text(JsonElement config, string name)
{
    return config.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

static void Append(string? logPath, string line)
{
    if (!string.IsNullOrWhiteSpace(logPath))
    {
        File.AppendAllText(logPath, line.TrimEnd() + Environment.NewLine);
    }
}

static string JsonString(string value) => JsonSerializer.Serialize(value);

// One scripted session/update, in the shape the Cursor CLI sends: a thought, an assistant chunk, or a tool call.
static string TraceLine(string? sessionId, JsonElement trace)
{
    var kind = Text(trace, "kind");
    Dictionary<string, object?> update = kind switch
    {
        "thought" => new()
        {
            ["sessionUpdate"] = "agent_thought_chunk",
            ["content"] = new Dictionary<string, object?> { ["type"] = "text", ["text"] = Text(trace, "text") ?? "" },
        },
        "tool" => ToolUpdate(trace),
        _ => new()
        {
            ["sessionUpdate"] = "agent_message_chunk",
            ["content"] = new Dictionary<string, object?> { ["type"] = "text", ["text"] = Text(trace, "text") ?? "" },
        },
    };
    return JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "session/update",
        ["params"] = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["update"] = update,
        },
    });
}

static Dictionary<string, object?> ToolUpdate(JsonElement trace)
{
    var update = trace.TryGetProperty("update", out var flag) && flag.ValueKind == JsonValueKind.True;
    var body = new Dictionary<string, object?>
    {
        ["sessionUpdate"] = update ? "tool_call_update" : "tool_call",
        ["toolCallId"] = Text(trace, "id") ?? "",
    };
    Add(body, "title", Text(trace, "title"));
    Add(body, "kind", Text(trace, "toolKind"));
    Add(body, "status", Text(trace, "status"));
    var path = Text(trace, "path");
    var command = Text(trace, "command");
    if (path is not null || command is not null)
    {
        var input = new Dictionary<string, object?>();
        if (path is not null)
        {
            input["path"] = path;
            body["locations"] = new object[] { new Dictionary<string, object?> { ["path"] = path } };
        }

        if (command is not null)
        {
            input["command"] = command;
        }

        body["rawInput"] = input;
    }

    var blocks = new List<Dictionary<string, object?>>();
    var oldText = Text(trace, "oldText");
    var newText = Text(trace, "newText");
    if (oldText is not null || newText is not null)
    {
        var diff = new Dictionary<string, object?> { ["type"] = "diff" };
        if (path is not null)
        {
            diff["path"] = path;
        }

        if (oldText is not null)
        {
            diff["oldText"] = oldText;
        }

        if (newText is not null)
        {
            diff["newText"] = newText;
        }

        blocks.Add(diff);
    }

    var output = Text(trace, "output");
    if (output is not null)
    {
        blocks.Add(new Dictionary<string, object?>
        {
            ["type"] = "content",
            ["content"] = new Dictionary<string, object?> { ["type"] = "text", ["text"] = output },
        });
    }

    if (blocks.Count > 0)
    {
        body["content"] = blocks;
    }

    return body;
}

static void Add(Dictionary<string, object?> body, string name, string? value)
{
    if (value is not null)
    {
        body[name] = value;
    }
}
