using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Media;

namespace HASS.Agent.Companion.Tests;

/// <summary>
/// A media session that is just closing is still in Windows' list for a moment and answers
/// with nothing. That was a "media state not published" warning; it is a session that does
/// not play and has no length now. And a warning of that kind names where it came from.
/// </summary>
public sealed class MediaSessionClosingTests
{
    [Fact]
    public void A_session_with_no_playback_info_does_not_play()
    {
        Assert.False(MediaSessionService.IsPlaying(null));
    }

    [Fact]
    public void A_session_with_no_timeline_has_no_length_and_no_position()
    {
        Assert.Equal((0d, 0d), MediaSessionService.TimelineOf(null));
    }

    [Fact]
    public void A_failure_in_a_method_is_named_by_that_method()
    {
        var failure = Assert.Throws<InvalidOperationException>(Fails);

        Assert.Equal("MediaSessionClosingTests.Fails", ExceptionPlace.Of(failure, typeof(MediaSessionClosingTests).Assembly));
    }

    [Fact]
    public async Task A_failure_in_an_async_method_or_a_lambda_is_named_by_the_method_that_was_written()
    {
        var inAsync = await Assert.ThrowsAsync<InvalidOperationException>(FailsLaterAsync);
        Assert.Equal("MediaSessionClosingTests.FailsLaterAsync", ExceptionPlace.Of(inAsync, typeof(MediaSessionClosingTests).Assembly));

        var inLambda = Assert.Throws<NullReferenceException>(FailsInALambda);
        Assert.Equal("MediaSessionClosingTests.FailsInALambda", ExceptionPlace.Of(inLambda, typeof(MediaSessionClosingTests).Assembly));
    }

    [Fact]
    public void A_failure_inside_someone_else_s_code_names_our_method_and_theirs()
    {
        var failure = Assert.Throws<FormatException>(ParsesWhatIsNoNumber);

        var place = ExceptionPlace.Of(failure, typeof(MediaSessionClosingTests).Assembly);

        Assert.StartsWith("MediaSessionClosingTests.ParsesWhatIsNoNumber (in ", place);
        Assert.EndsWith(")", place);
    }

    [Fact]
    public void A_failure_that_was_never_thrown_has_no_place()
    {
        Assert.Equal("an unknown place", ExceptionPlace.Of(new InvalidOperationException("made, not thrown")));
    }

    private static void Fails() => throw new InvalidOperationException("here");

    private static async Task FailsLaterAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("later");
    }

    private static void FailsInALambda()
    {
        string?[] sessions = [null];
        _ = sessions.FirstOrDefault(session => session!.Length > 0);
    }

    private static void ParsesWhatIsNoNumber() => _ = int.Parse("not a number", System.Globalization.CultureInfo.InvariantCulture);
}
