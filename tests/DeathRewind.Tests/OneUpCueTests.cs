using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    public class OneUpCueTests
    {
        [Fact]
        public void BeforeLevelStarted_NeverPlays()
        {
            var cue = new OneUpCue();

            Assert.False(cue.ShouldPlay(true));
        }

        [Fact]
        public void LevelStarted_PlaysOnceWhenOpportunityAppears()
        {
            var cue = new OneUpCue();
            cue.OnLevelStarted();

            Assert.True(cue.ShouldPlay(true));
        }

        [Fact]
        public void SameLevel_PlaysOnlyOnce()
        {
            var cue = new OneUpCue();
            cue.OnLevelStarted();

            Assert.True(cue.ShouldPlay(true));
            Assert.False(cue.ShouldPlay(true));
            Assert.False(cue.ShouldPlay(true));
        }

        [Fact]
        public void NoOpportunity_DoesNotPlay()
        {
            var cue = new OneUpCue();
            cue.OnLevelStarted();

            Assert.False(cue.ShouldPlay(false));
        }

        [Fact]
        public void OpportunityAppearingLater_PlaysOnce()
        {
            var cue = new OneUpCue();
            cue.OnLevelStarted();

            Assert.False(cue.ShouldPlay(false));
            Assert.True(cue.ShouldPlay(true));
            Assert.False(cue.ShouldPlay(true));
        }

        [Fact]
        public void NextLevel_PlaysAgain()
        {
            var cue = new OneUpCue();
            cue.OnLevelStarted();
            Assert.True(cue.ShouldPlay(true));

            cue.OnLevelEnded();
            Assert.False(cue.ShouldPlay(true));

            cue.OnLevelStarted();
            Assert.True(cue.ShouldPlay(true));
        }

        [Fact]
        public void LevelEnded_MakesCueInert()
        {
            var cue = new OneUpCue();
            cue.OnLevelStarted();
            Assert.True(cue.ShouldPlay(true));

            cue.OnLevelEnded();

            Assert.False(cue.ShouldPlay(true));
        }
    }
}
