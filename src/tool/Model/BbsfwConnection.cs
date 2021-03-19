using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BBSFW.Model
{

	public class ComPort
	{
		public string Name { get; private set; }

		public string Description { get; private set; }

		public ComPort(string name, string description)
		{
			Name = name;
			Description = description;
		}
	}

	public class BbsfwConnection
	{

		private const int REQUEST_TYPE_READ =			0x01;
		private const int REQUEST_TYPE_WRITE =			0x02;

		private const int RESPONSE_TYPE_READ = 			0x01;
		private const int RESPONSE_TYPE_WRITE = 		0x02;

		private const int EVENT_LOG_ENTRY =				0xee;
		private const int EVENT_LOG_DATA_ENTRY =		0xed;

		private const int OPCODE_READ_FW_VERSION =		0x01;
		private const int OPCODE_READ_EVTLOG_ENABLE =	0x02;
		private const int OPCODE_READ_CONFIG =			0x03;

		private const int OPCODE_WRITE_EVTLOG_ENABLE =	0xf0;
		private const int OPCODE_WRITE_CONFIG =			0xf1;


		private enum ParseResult
		{
			Keep,
			Discard,
			Complete
		}

		private SerialPort _port = null;
		private volatile bool _isConnecting = false;
		private volatile bool _isConnected = false;

		private DateTime _lastRecv = DateTime.Now;
		private List<byte> _rxBuffer = new List<byte>();


		private CompletionQueue<Configuration> _readConfigCq = new CompletionQueue<Configuration>();
		private CompletionQueue<bool> _writeConfigCq = new CompletionQueue<bool>();


		public bool IsConnected
		{
			get
			{
				return _isConnected;
			}
		}


		public event Action<string, int>		Connected;
		public event Action						Disconnected;


		public event Action<EventLogEntry>		EventLog;



		public static List<ComPort> GetComPorts()
		{
			var result = new List<ComPort>();

			using (var searcher = new ManagementObjectSearcher("SELECT * FROM WIN32_SerialPort"))
			{
				var portNames = SerialPort.GetPortNames();
				var ports = searcher.Get().Cast<ManagementBaseObject>().ToList();

				foreach (var name in portNames)
				{
					var port = ports.FirstOrDefault(p => String.Equals(p["DeviceID"].ToString(), name));
					if (port != null)
					{
						result.Add(new ComPort(name, port["Caption"].ToString()));
					}
				}
			}

			return result;
		}



		public async Task<bool> Connect(ComPort port, TimeSpan timeout)
		{
			_isConnected = false;
			_isConnecting = true;
			_port = new SerialPort(port.Name, 1200);
			_port.DataReceived += OnDataReceived;
			_port.Open();

			var connected = await Task.Run(() => SetupConnection(timeout));
			if (!connected)
			{
				Close();
			}

			return connected;
		}

		public void Close()
		{
			if (_port != null)
			{
				_port.Close();
				_port.DataReceived += OnDataReceived;
				_port = null;

				_isConnected = false;
				_isConnecting = false;

				lock (_rxBuffer)
				{
					_rxBuffer.Clear();
				}

				Disconnected?.Invoke();
			}
		}


		public async Task<RequestResult<Configuration>> ReadConfiguration(TimeSpan timeout)
		{
			SendReadRequest(OPCODE_READ_CONFIG);
			return await _readConfigCq.WaitResponse(timeout);
		}

		public async Task<RequestResult<bool>> WriteConfiguration(Configuration configuration, TimeSpan timeout)
		{
			SendWriteConfigRequest(configuration);
			return await _writeConfigCq.WaitResponse(timeout);
		}



		private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
		{
			// check for communication error and reset
			if (_rxBuffer.Any() && DateTime.Now - _lastRecv > TimeSpan.FromMilliseconds(100))
			{
				_rxBuffer.Clear();
			}

			bool close = false;
			lock(_rxBuffer)
			{
				while (_port.BytesToRead > 0)
				{
					_lastRecv = DateTime.Now;

					var b = _port.ReadByte();
					if (b == -1)
					{
						close = true;
						break;
					}
					else
					{
						_rxBuffer.Add((byte)b);
					}
				}
			}
			
			if (close)
			{
				Close();
			}
			else
			{
				ProcessInputBuffer();
			}
		}


		private void ProcessInputBuffer()
		{
			lock(_rxBuffer)
			{
				var result = ProcessMessage();
				if (result == ParseResult.Discard || result == ParseResult.Complete)
				{
					_rxBuffer.Clear();
				}
			}
		}


		private ParseResult ProcessMessage()
		{
			if (_rxBuffer.Count < 1)
			{
				return ParseResult.Keep;
			}

			switch(_rxBuffer[0])
			{
				case RESPONSE_TYPE_READ:
					return ProcessReadResponse();
				case RESPONSE_TYPE_WRITE:
					return ProcessWriteResponse();
				case EVENT_LOG_ENTRY:
				case EVENT_LOG_DATA_ENTRY:
					return ProcessEventLogEntry();
			}

			return ParseResult.Discard;
		}


		private ParseResult ProcessReadResponse()
		{
			if (_rxBuffer.Count < 2)
			{
				return ParseResult.Keep;
			}

			switch(_rxBuffer[1])
			{
			case OPCODE_READ_FW_VERSION:
				return ProcessReadResponseFwVersion();
			case OPCODE_READ_EVTLOG_ENABLE:
				return ProcessReadResponseEvtlogEnable();
			case OPCODE_READ_CONFIG:
				return ProcessReadResponseConfig();
			}

			return ParseResult.Discard;
		}

		private ParseResult ProcessReadResponseFwVersion()
		{
			if (_rxBuffer.Count < 7)
			{
				return ParseResult.Keep;
			}

			if (ComputeChecksum(_rxBuffer, 6) == _rxBuffer[6])
			{
				int major = _rxBuffer[2];
				int minor = _rxBuffer[3];
				int patch = _rxBuffer[4];
				int config = _rxBuffer[5];

				if (_isConnecting)
				{
					_isConnecting = false;
					_isConnected = true;
					Connected?.Invoke(String.Format("{0}.{1}.{2}", major, minor, patch), config);
				}
			}
			else
			{
				return ParseResult.Discard;
			}

			return ParseResult.Complete;
		}

		private ParseResult ProcessReadResponseEvtlogEnable()
		{
			if (_rxBuffer.Count < 4)
			{
				return ParseResult.Keep;
			}

			// not used

			return ParseResult.Complete;
		}

		private ParseResult ProcessReadResponseConfig()
		{
			if (_rxBuffer.Count > 3)
			{
				var version = _rxBuffer[2];
				var size = _rxBuffer[3];

				if (version != Configuration.Version || size != Configuration.ByteSize)
				{
					return ParseResult.Discard;
				}
			}

			if (_rxBuffer.Count < (4 + Configuration.ByteSize + 1))
			{
				return ParseResult.Keep;
			}

			if (ComputeChecksum(_rxBuffer, 4 + Configuration.ByteSize) == _rxBuffer[4 + Configuration.ByteSize])
			{
				var cfg = new Configuration();
				cfg.ParseFromBuffer(_rxBuffer.Skip(4).Take(Configuration.ByteSize).ToArray());

				_readConfigCq.Complete(cfg);
			}
			else
			{
				return ParseResult.Discard;
			}

			return ParseResult.Complete;
		}


		private ParseResult ProcessWriteResponse()
		{
			if (_rxBuffer.Count < 2)
			{
				return ParseResult.Keep;
			}

			switch (_rxBuffer[1])
			{
				case OPCODE_WRITE_EVTLOG_ENABLE:
					return ProcessWriteResponseEvtlogEnable();
				case OPCODE_WRITE_CONFIG:
					return ProcessWriteResponseConfig();
			}

			return ParseResult.Discard;
		}

		private ParseResult ProcessWriteResponseEvtlogEnable()
		{
			if (_rxBuffer.Count < 4)
			{
				return ParseResult.Keep;
			}

			// don't care

			return ParseResult.Complete;
		}

		private ParseResult ProcessWriteResponseConfig()
		{
			if (_rxBuffer.Count < 4)
			{
				return ParseResult.Keep;
			}

			_writeConfigCq.Complete(_rxBuffer[2] != 0);

			return ParseResult.Complete;
		}


		private ParseResult ProcessEventLogEntry()
		{
			if (_rxBuffer[0] == EVENT_LOG_ENTRY)
			{
				if (_rxBuffer.Count < 2)
				{
					return ParseResult.Discard;
				}

				EventLog?.Invoke(new EventLogEntry(_rxBuffer[1]));
			}
			else if (_rxBuffer[0] == EVENT_LOG_DATA_ENTRY)
			{
				if (_rxBuffer.Count < 4)
				{
					return ParseResult.Keep;
				}

				int data = _rxBuffer[2] << 8 | _rxBuffer[3];
				EventLog?.Invoke(new EventLogEntry(_rxBuffer[1], data));
			}

			return ParseResult.Complete;
		}


		private void SendReadRequest(byte opcode)
		{
			var buf = new List<byte>();
			buf.Add(REQUEST_TYPE_READ);
			buf.Add(opcode);
			buf.Add(ComputeChecksum(buf, buf.Count));

			_port.Write(buf.ToArray(), 0, buf.Count);
		}

		private void SendEventLogEnableRequest(bool enable)
		{
			var buf = new List<byte>();
			buf.Add(REQUEST_TYPE_WRITE);
			buf.Add(OPCODE_WRITE_EVTLOG_ENABLE);
			buf.Add((byte)(enable ? 1 : 0));
			buf.Add(ComputeChecksum(buf, buf.Count));

			_port.Write(buf.ToArray(), 0, buf.Count);
		}

		private void SendWriteConfigRequest(Configuration config)
		{
			var cfgarr = config.WriteToBuffer();

			var buf = new List<byte>();
			buf.Add(REQUEST_TYPE_WRITE);
			buf.Add(OPCODE_WRITE_CONFIG);
			buf.Add((byte)Configuration.Version);
			buf.Add((byte)cfgarr.Length);
			buf.AddRange(cfgarr);
			buf.Add(ComputeChecksum(buf, buf.Count));

			_port.Write(buf.ToArray(), 0, buf.Count);
		}


		private bool SetupConnection(TimeSpan timeout)
		{
			var start = DateTime.Now;
			while (_isConnecting && !_isConnected)
			{
				if (DateTime.Now - start > timeout)
				{
					return false;
				}

				SendReadRequest(OPCODE_READ_FW_VERSION);
				Thread.Sleep(200);
			}

			return true;
		}


		private static byte ComputeChecksum(List<byte> buffer, int length)
		{
			unchecked
			{
				byte result = 0;
				for (int i = 0; i < length; i++)
				{
					result += buffer[i];
				}

				return result;
			}
		}

	}
}