using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using P3DAIPilot.Core;

namespace P3DAIPilot.Bridge
{
    public sealed class PdkAdapter : ISimulatorTransport
    {
        private sealed class Pending { public Message Message; public TaskCompletionSource<uint> Completion; }
        private readonly object _sync=new object();
        private readonly Queue<Pending> _requests=new Queue<Pending>();
        private readonly Dictionary<ulong,TaskCompletionSource<uint>> _acks=new Dictionary<ulong,TaskCompletionSource<uint>>();
        private readonly string _pipeName;
        private readonly int _connectMs, _commandMs;
        private NamedPipeClientStream _pipe;
        private bool _connected, _disposed, _controlAvailable;
        private ulong _sequence, _receiveSequence, _lease, _supported;
        private byte[] _latestCommand;
        private AircraftState _state;
        private SimulatorTransportHealth _health=new SimulatorTransportHealth {Name="PDK",Version="IPC v1"};
        private readonly bool _requireOwnedTarget;
        private bool _ownedTarget;
        public bool IsOwnedTarget { get { lock(_sync)return _ownedTarget; } }
        public PdkAdapter(string pipeName="P3D-AIPilot-Telemetry",int connectTimeoutMs=1000,int commandTimeoutMs=250,bool requireOwnedTarget=false)
        {
            if (string.IsNullOrWhiteSpace(pipeName)||pipeName.Contains("\\")||pipeName.Length>128) throw new ArgumentException("Invalid local pipe name.");
            if(connectTimeoutMs<100||connectTimeoutMs>30000||commandTimeoutMs<50||commandTimeoutMs>500) throw new ArgumentOutOfRangeException();
            _requireOwnedTarget=requireOwnedTarget; _pipeName=pipeName; _connectMs=connectTimeoutMs; _commandMs=commandTimeoutMs;
        }
        public string Name { get { return "PDK"; } }
        public ulong SupportedTelemetryMask { get { lock(_sync)return _supported; } }
        public bool HasAuthority { get { lock(_sync)return _lease!=0; } }
        public event Action<string> StatusChanged;
        public void Connect()
        {
            lock(_sync)
            {
                if(_disposed)throw new ObjectDisposedException(Name);
                if(_connected)return;
                _health.ReconnectAttempts++;
                _pipe=new NamedPipeClientStream(".",_pipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
                try
                {
                    _pipe.Connect(_connectMs);
                    _sequence=0; _receiveSequence=0; _lease=0; _controlAvailable=false; _ownedTarget=false; _state=null;
                    var hello=new Message {Type=MessageType.Hello,Sequence=++_sequence,TimestampMs=Protocol.NowMs};
                    byte[] encoded=Protocol.Encode(hello);
                    using(var deadline=new CancellationTokenSource(_connectMs))
                    {
                        _pipe.WriteAsync(encoded,0,encoded.Length,deadline.Token).GetAwaiter().GetResult();
                        Message ack=Protocol.ReadAsync(_pipe,deadline.Token).GetAwaiter().GetResult();
                        if(ack.Type!=MessageType.HelloAck||ack.Payload.Length!=0||ack.Sequence==0)throw new InvalidDataException("HELLO rejected.");
                        _receiveSequence=ack.Sequence;
                    }
                    _connected=true; _health.IsConnected=true; _health.HasTelemetry=false;
                    _health.ConnectedUtc=DateTime.UtcNow; _health.LastTelemetryUtc=DateTime.MinValue; _health.LastError="";
                }
                catch(Exception ex) { _pipe.Dispose();_pipe=null;_health.LastError=ex.Message;_health.ConsecutiveFailures++;throw; }
            }
        }
        public void Pump(CancellationToken token)
        {
            using(var stop=CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task reader=ReadLoop(stop.Token),writer=WriteLoop(stop.Token);
                try { Task.WhenAny(reader,writer).GetAwaiter().GetResult(); }
                finally
                {
                    stop.Cancel(); Disconnect("IPC pump stopped; authority revoked.");
                    try { Task.WhenAll(reader,writer).GetAwaiter().GetResult(); }
                    catch(Exception ex) { lock(_sync) _health.LastError=ex.Message; StatusChanged?.Invoke("PDK: "+ex.Message); }
                }
            }
        }
        private async Task ReadLoop(CancellationToken token)
        {
            while(!token.IsCancellationRequested)
            {
                NamedPipeClientStream pipe;lock(_sync)pipe=_pipe;
                if(pipe==null)return;
                Message m;
                using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token))
                { deadline.CancelAfter(2000); m=await Protocol.ReadAsync(pipe,deadline.Token).ConfigureAwait(false); }
                lock(_sync)
                {
                    if(m.Sequence<=_receiveSequence||Math.Abs(Protocol.NowMs-m.TimestampMs)>5000)throw new InvalidDataException("Stale bridge message.");
                    _receiveSequence=m.Sequence;
                    using(var r=new BinaryReader(new MemoryStream(m.Payload)))
                    {
                        if(m.Type==MessageType.Telemetry)
                        {
                            ulong mask; AircraftState s=WireSchema.Read(r,out mask);
                            if(r.BaseStream.Position!=r.BaseStream.Length)throw new InvalidDataException("Trailing telemetry.");
                            _supported=mask;
                            s.UtcTimestamp=DateTimeOffset.FromUnixTimeMilliseconds(m.TimestampMs).UtcDateTime;
                            s.TelemetrySequence=checked((long)m.Sequence);s.ControlRole=_ownedTarget?"ai":"human";_state=s;
                            _health.HasTelemetry=true;_health.LastTelemetryUtc=s.UtcTimestamp;_health.TelemetrySamples++;
                        }
                        else if(m.Type==MessageType.Capabilities)
                        {
                            if(m.Payload.Length!=16)throw new InvalidDataException("Invalid capabilities.");
                            _supported=r.ReadUInt64();uint flags=r.ReadUInt32();_controlAvailable=(flags&1)!=0;_ownedTarget=(flags&2)!=0;r.ReadUInt32();
                            _health.Capabilities=SimulatorCapabilities.Telemetry|(_controlAvailable?SimulatorCapabilities.Controls:0);
                        }
                        else if(m.Type==MessageType.CommandAck)
                        {
                            if(m.Payload.Length!=20)throw new InvalidDataException("Invalid acknowledgement.");
                            ulong request=r.ReadUInt64();uint result=r.ReadUInt32();_lease=r.ReadUInt64();
                            TaskCompletionSource<uint> completion;
                            if(_acks.TryGetValue(request,out completion)){_acks.Remove(request);completion.TrySetResult(result);}
                            if(result!=0)_health.LastError="Native command result="+result;
                            else {_health.LastCommandUtc=DateTime.UtcNow;_health.CommandsSent++;}
                        }
                        else if(m.Type==MessageType.Status) {if(m.Payload.Length!=8)throw new InvalidDataException("Invalid status.");_lease=r.ReadUInt64();}
                        else if(m.Type==MessageType.Error) throw new InvalidDataException("Native bridge rejected session.");
                    }
                }
            }
        }
        private async Task WriteLoop(CancellationToken token)
        {
            long lastHeartbeat=0;
            while(!token.IsCancellationRequested)
            {
                Pending pending=null;NamedPipeClientStream pipe;
                lock(_sync)
                {
                    pipe=_pipe;if(pipe==null)return;
                    if(_requests.Count>0)pending=_requests.Dequeue();
                    else if(Protocol.NowMs-lastHeartbeat>=250){lastHeartbeat=Protocol.NowMs;pending=new Pending{Message=new Message{Type=MessageType.Heartbeat}};}
                    else if(_latestCommand!=null){pending=new Pending{Message=new Message{Type=MessageType.Command,Payload=_latestCommand}};_latestCommand=null;}
                    if(pending!=null)
                    {
                        pending.Message.Sequence=++_sequence;pending.Message.TimestampMs=Protocol.NowMs;
                        if(pending.Completion!=null)_acks.Add(_sequence,pending.Completion);
                    }
                }
                if(pending!=null)
                {
                    byte[] bytes=Protocol.Encode(pending.Message);
                    using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token))
                    {deadline.CancelAfter(500);await pipe.WriteAsync(bytes,0,bytes.Length,deadline.Token).ConfigureAwait(false);}
                }
                await Task.Delay(10,token).ConfigureAwait(false);
            }
        }
        private void AuthorityRequest(MessageType type)
        {
            var done=new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock(_sync)
            {
                if(!_connected)throw new InvalidOperationException("PDK not connected.");
                if(type==MessageType.ControlAcquire&&_requireOwnedTarget&&!_ownedTarget)throw new InvalidOperationException("Mission requires an owned non-user aircraft; user controls are blocked.");
                if(type==MessageType.ControlAcquire&&!_controlAvailable)throw new InvalidOperationException("Native controls disabled or aircraft capabilities incomplete.");
                if(_requests.Count>=8||_acks.Count>=8)throw new InvalidOperationException("Authority queue is full.");
                _latestCommand=null;if(type==MessageType.ControlRelease)_lease=0;
                _requests.Enqueue(new Pending{Message=new Message{Type=type},Completion=done});
            }
            if(!done.Task.Wait(2000)){Disconnect("Authority acknowledgement timed out.");throw new TimeoutException("Authority acknowledgement timed out.");}
            if(done.Task.Result!=0)throw new InvalidOperationException("Authority request rejected: "+done.Task.Result);
        }
        public void AcquireControl(){AuthorityRequest(MessageType.ControlAcquire);}
        public void ReleaseControl(){if(GetHealth().IsConnected)AuthorityRequest(MessageType.ControlRelease);}
        public void Send(ControlCommand command)
        {
            if(command==null||!command.IsFinite())throw new ArgumentException("Invalid control command.");
            ControlCommand copy=command.Clone();copy.Clamp();
            lock(_sync)
            {
                if(!_connected||_lease==0||!_controlAvailable)throw new InvalidOperationException("Explicit PDK authority acquisition required.");
                _latestCommand=Protocol.Payload(w=>{w.Write(_lease);w.Write(Protocol.NowMs+_commandMs);w.Write(1u);w.Write(0);
                    w.Write(copy.Aileron);w.Write(copy.Elevator);w.Write(copy.Rudder);w.Write(copy.Throttle01);w.Write(copy.Brakes01);w.Write(copy.Flaps01);w.Write(copy.GearDown?1u:0u);});
            }
        }
        public void ResetScenario(){throw new NotSupportedException("PDK scenario reset is not advertised by this bridge.");}
        public AircraftState GetLatestState(){lock(_sync)return _state;}
        public SimulatorTransportHealth GetHealth(){lock(_sync)return _health.Clone();}
        public void Disconnect(string reason)
        {
            lock(_sync)
            {
                _connected=false;_health.IsConnected=false;_health.HasTelemetry=false;_controlAvailable=false;_lease=0;_latestCommand=null;_state=null;
                _health.LastError=reason;
                if(_pipe!=null){_pipe.Dispose();_pipe=null;}
                foreach(var done in _acks.Values)done.TrySetResult(uint.MaxValue);_acks.Clear();
                while(_requests.Count>0)_requests.Dequeue().Completion?.TrySetResult(uint.MaxValue);
            }
        }
        public void Dispose(){Disconnect("Disposed.");lock(_sync)_disposed=true;}
    }
}
