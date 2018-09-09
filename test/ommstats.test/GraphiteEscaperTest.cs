using System;
using Xunit;

namespace eventphone.ommstats.test
{
    public class GraphiteEscaperTest
    {
        readonly GraphiteEscaper _escaper = new GraphiteEscaper();

        [Fact]
        public void EscapeKeepsValidChars()
        {
            var name = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";
            var escaped = _escaper.Escape(name);
            Assert.Equal(name, escaped);
        }

        [Fact]
        public void EscapeRemovesNoChar()
        {
            var name = "\"M<>\"\\a/ry/ h**ad:>> a\\/:*?\"<>| li*tt|le|| la\"mb.?";
            var escaped = _escaper.Escape(name);
            Assert.Equal(name.Length, escaped.Length);
            Assert.Equal("_M____a_ry__h__ad____a__________li_tt_le___la_mb__", escaped);
        }
    }
}
