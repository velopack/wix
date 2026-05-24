namespace wixc;

using System;
using WixToolset.Data;
using WixToolset.Extensibility;
using WixToolset.Extensibility.Services;

internal sealed class MessageListener : IMessageListener
{
    private const string Prefix = "WIX";
    private const string AppName = "wixc7.exe";

    public void Write(Message message)
    {
        var filename = message.SourceLineNumbers?.FileName ?? AppName;
        if (message.SourceLineNumbers?.LineNumber is int line)
        {
            filename = string.Concat(filename, "(", line, ")");
        }

        var level = message.Level.ToString().ToLowerInvariant();
        var output = message.Level >= MessageLevel.Warning ? Console.Out : Console.Error;
        output.WriteLine("{0} : {1} {2}{3:0000}: {4}", filename, level, Prefix, message.Id, message.ToString());
    }

    public void Write(string message)
    {
        Console.Out.WriteLine(message);
    }

    public MessageLevel CalculateMessageLevel(IMessaging messaging, Message message, MessageLevel defaultMessageLevel)
    {
        return defaultMessageLevel;
    }
}
