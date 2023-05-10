﻿using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StableUI.Accounts;
using StableUI.Backends;
using StableUI.Core;
using StableUI.DataHolders;
using StableUI.Utils;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace StableUI.WebAPI;

/// <summary>Text-to-Image API routes</summary>
public static class T2IAPI
{
    public static void Register()
    {
        API.RegisterAPICall(GenerateText2Image);
        API.RegisterAPICall(GenerateText2ImageWS);
        API.RegisterAPICall(ListImages);
    }

#pragma warning disable CS1998 // "CS1998 Async method lacks 'await' operators and will run synchronously"

    /// <summary>API route to generate images with WebSocket updates.</summary>
    public static async Task<JObject> GenerateText2ImageWS(WebSocket socket, Session session, int images, T2IParams user_input)
    {
        await foreach ((string img, JObject err) in GenT2I_Internal(session, images, user_input))
        {
            if (img is not null)
            {
                await socket.SendJson(new JObject() { ["image"] = img }, TimeSpan.FromMinutes(1)); // TODO: Configurable timeout
            }
            if (err is not null)
            {
                await socket.SendJson(err, TimeSpan.FromMinutes(1));
                break;
            }
        }
        return null;
    }

    /// <summary>API route to generate images directly as HTTP.</summary>
    public static async Task<JObject> GenerateText2Image(Session session, int images, T2IParams user_input)
    {
        List<string> outputs = new();
        await foreach ((string img, JObject err) in GenT2I_Internal(session, images, user_input))
        {
            if (img is not null)
            {
                outputs.Add(img);
            }
            if (err is not null)
            {
                return err;
            }
        }
        return new JObject() { ["images"] = JToken.FromObject(outputs) };
    }

    /// <summary>Internal route for generating images.</summary>
    public static async IAsyncEnumerable<(string, JObject)> GenT2I_Internal(Session session, int images, T2IParams user_input)
    {
        ConcurrentQueue<string> allOutputs = new();
        user_input = user_input.Clone();
        if (user_input.Seed == -1)
        {
            user_input.Seed = Random.Shared.Next(int.MaxValue);
        }
        JObject errorOut = null;
        List<Task> tasks = new();
        int max_degrees = 4; // TODO: Configure max degrees parallel based on user limit / global limit / backend count
        for (int i = 0; i < images; i++)
        {
            tasks.RemoveAll(t => t.IsCompleted);
            if (tasks.Count > max_degrees)
            {
                await Task.WhenAny(tasks);
            }
            if (Volatile.Read(ref errorOut) is not null)
            {
                yield return (null, errorOut);
                yield break;
            }
            while (allOutputs.TryDequeue(out string output))
            {
                yield return (output, null);
            }
            int index = i;
            tasks.Add(Task.Run(async () =>
            {
                T2IBackendAccess backend;
                try
                {
                    backend = Program.Backends.GetNextT2IBackend(TimeSpan.FromMinutes(2)); // TODO: Max timespan configurable
                }
                catch (InvalidOperationException ex)
                {
                    Volatile.Write(ref errorOut, new JObject() { ["error"] = $"Invalid operation: {ex.Message}" });
                    return;
                }
                catch (TimeoutException)
                {
                    Volatile.Write(ref errorOut, new JObject() { ["error"] = "Timeout! All backends are occupied with other tasks." });
                    return;
                }
                if (Volatile.Read(ref errorOut) is not null)
                {
                    return;
                }
                using (backend)
                {
                    T2IParams thisParams = user_input.Clone();
                    thisParams.Seed += index;
                    Image[] outputs = await backend.Backend.Generate(thisParams);
                    foreach (Image image in outputs)
                    {
                        string url = session.SaveImage(image, thisParams);
                        if (url == "ERROR")
                        {
                            Volatile.Write(ref errorOut, new JObject() { ["error"] = $"Server failed to save images." });
                            return;
                        }
                        allOutputs.Enqueue(url);
                    }
                }
            }));
        }
        while (tasks.Any())
        {
            await Task.WhenAny(tasks);
            tasks.RemoveAll(t => t.IsCompleted);
            while (allOutputs.TryDequeue(out string output))
            {
                yield return (output, null);
            }
        }
        errorOut = Volatile.Read(ref errorOut);
        if (errorOut is not null)
        {
            yield return (null, errorOut);
            yield break;
        }
    }

    public static HashSet<string> ImageExtensions = new() { "png", "jpg" };

    /// <summary>API route to get a list of available history images.</summary>
    public static async Task<JObject> ListImages(Session session, string path="")
    {
        (path, string consoleError, string userError) = WebServer.CheckOutputFilePath(path, session.User.UserID);
        Console.WriteLine($"Path: {path}");
        if (consoleError is not null)
        {
            Logs.Error(consoleError);
            return new JObject() { ["error"] = userError };
        }
        try
        {
            return new JObject()
            {
                ["folders"] = JToken.FromObject(Directory.EnumerateDirectories(path).Select(Path.GetFileName).ToList()),
                ["images"] = JToken.FromObject(Directory.EnumerateFiles(path).Where(f => ImageExtensions.Contains(f.AfterLast('.'))).Select(f => f.Replace('\\', '/').AfterLast('/')).Select(f => new JObject() { ["src"] = f, ["batch_id"] = 0 }).ToList())
            };
        }
        catch (Exception ex)
        {
            if (ex is FileNotFoundException || ex is DirectoryNotFoundException || ex is PathTooLongException)
            {
                return new JObject() { ["error"] = "404, path not found." };
            }
            else
            {
                Logs.Error($"Failed to create output image file list '{path}': {ex}");
                return new JObject() { ["error"] = "Error reading file list." };
            }
        }
    }
}