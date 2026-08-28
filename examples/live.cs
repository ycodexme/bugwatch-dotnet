using Bugwatch;
var client = new BugwatchClient("https://fc52164cd6faf5f7244edf4b7f6ac910@bugwatch-api.loadmindx.com/api/1");
var id = client.CaptureMessage(".NET SDK live validation", Level.Error);
Console.WriteLine($"DOTNET_ID={id}");
client.Flush();
client.Dispose();
