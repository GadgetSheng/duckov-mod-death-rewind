using DeathRewind.Core;
using Xunit;

namespace DeathRewind.Tests
{
    public class RewindClockTests
    {
        [Fact]
        public void Tick_AccumulatesEffectiveTime()
        {
            var clock = new RewindClock();
            clock.Tick(10.0);
            clock.Tick(12.5);
            Assert.Equal(12.5, clock.CurrentTick, 6);
            Assert.Equal(12.5, clock.RawTime, 6);
        }

        [Fact]
        public void Freeze_SuspendsEffectiveTimeButKeepsRawTime()
        {
            var clock = new RewindClock();
            clock.Tick(10.0);
            clock.Freeze();
            clock.Tick(20.0);
            Assert.Equal(10.0, clock.CurrentTick, 6);   // 冻结时长不计入
            Assert.Equal(20.0, clock.RawTime, 6);
            clock.Resume();
            clock.Tick(21.0);
            Assert.Equal(11.0, clock.CurrentTick, 6);
        }

        [Fact]
        public void Age_IsUnaffectedByHowLongClockHasRun()
        {
            var clock = new RewindClock();
            clock.Tick(300.0);
            Assert.Equal(200.0, clock.Age(100.0), 6);
        }
    }
}
