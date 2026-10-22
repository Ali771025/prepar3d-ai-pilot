using System;
using System.Threading;
using System.Threading.Tasks;
using P3DAIPilot.Core;
namespace P3DAIPilot.Bridge
{
    public enum ConnectionState { Stopped, Starting, Discovering, Connecting, Connected, Degraded, Reconnecting, Faulted, ShuttingDown }
    // One task owns connection creation and the pump. No timers or overlapping retries.
    public sealed class ConnectionSupervisor : IDisposable
    {
        private readonly ISimulatorTransport _adapter;
        private readonly CancellationTokenSource _stop=new CancellationTokenSource();
        private Task _task;
        private int _state;
        public ConnectionState State { get { return (ConnectionState)Volatile.Read(ref _state); } }
        public event Action<string> StatusChanged;
        public ConnectionSupervisor(ISimulatorTransport adapter){_adapter=adapter??throw new ArgumentNullException(nameof(adapter));}
        private void Set(ConnectionState state,string error=""){Volatile.Write(ref _state,(int)state);StatusChanged?.Invoke(state+": "+error);}
        public void Start()
        {
            if(_task!=null)throw new InvalidOperationException("Supervisor already started.");
            Set(ConnectionState.Starting);_task=Task.Run(async()=>
            {
                int failures=0;int[] backoff={1000,2000,5000,10000,30000};
                while(!_stop.IsCancellationRequested)
                {
                    Set(ConnectionState.Discovering);
                    try
                    {
                        Set(ConnectionState.Connecting);_adapter.Connect();Set(ConnectionState.Connected);
                        DateTime opened=DateTime.UtcNow;_adapter.Pump(_stop.Token);
                        if((DateTime.UtcNow-opened).TotalSeconds>=10)failures=0;
                    }
                    catch(Exception ex){if(!_stop.IsCancellationRequested)Set(ConnectionState.Degraded,ex.Message);}
                    finally{_adapter.Disconnect("Connection session ended; authority disabled.");}
                    if(_stop.IsCancellationRequested)break;
                    Set(ConnectionState.Reconnecting);
                    try{await Task.Delay(backoff[Math.Min(failures++,backoff.Length-1)],_stop.Token).ConfigureAwait(false);}
                    catch(OperationCanceledException){break;}
                }
                Set(ConnectionState.Stopped);
            });
        }
        public void Dispose()
        {
            Set(ConnectionState.ShuttingDown);_stop.Cancel();_adapter.Disconnect("Supervisor shutdown.");
            _task?.GetAwaiter().GetResult();_adapter.Dispose();_stop.Dispose();Set(ConnectionState.Stopped);
        }
    }
}
