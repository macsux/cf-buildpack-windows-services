using System;
using System.IO;
using System.Text;
using Xunit.Abstractions;

namespace WindowsServices.Tests
{
    public class ConsoleOutputCollector : TextWriter
    {
        readonly ITestOutputHelper _output;
        private readonly StringBuilder _buffer = new StringBuilder();

        public string Output => _buffer.ToString();

        public ConsoleOutputCollector(ITestOutputHelper output)
        {
            _output = output;
            Console.SetOut(this);
            Console.SetError(this);
        }

        public override Encoding Encoding => Encoding.Default;

        public override void WriteLine(string message)
        {
            _output.WriteLine(message);
            _buffer.AppendLine(message);
        }

        public override void WriteLine(string format, params object[] args)
        {
            _output.WriteLine(format, args);
            _buffer.AppendFormat(format, args);
        }
    }
}