namespace SampleSolution.Core.Interfaces;

// The write call lives here. Counting per declaration would leave the file above looking read-only.
public partial class SplitWriteGreetingConsumer
{
    public async Task RecordAsync(int userId, string message) =>
        await _greetings.RecordGreetingAsync(userId, message);
}
