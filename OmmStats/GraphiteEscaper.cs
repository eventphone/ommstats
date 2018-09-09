namespace OmmStats
{
    public class GraphiteEscaper
    {
        private static readonly bool[] _lookup;

        static GraphiteEscaper()
        {
            _lookup = new bool[123];
            for (char c = '0'; c <= '9'; c++) _lookup[c] = true;
            for (char c = 'A'; c <= 'Z'; c++) _lookup[c] = true;
            for (char c = 'a'; c <= 'z'; c++) _lookup[c] = true;
            _lookup['_'] = true;
        }

        public string Escape(string name)
        {
            char[] buffer = new char[name.Length];
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (_lookup.Length > c && _lookup[c])
                {
                    buffer[i] = c;
                }
                else
                {
                    buffer[i] = '_';
                }
            }
            return new string(buffer);
        }
    }
}