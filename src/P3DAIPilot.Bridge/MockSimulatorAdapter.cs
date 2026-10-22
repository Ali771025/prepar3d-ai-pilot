using System;
using System.Threading;
using P3DAIPilot.Core;
namespace P3DAIPilot.Bridge
{
    public sealed class MockSimulatorAdapter : ISimulatorTransport
    {
        private readonly object _sync=new object();
        private AircraftState _state;
        private bool _connected;
        private long _samples;
        public string Name { get { return "MOCK (synthetic)"; } }
        public event Action<string> StatusChanged;
        public void Connect(){lock(_sync)_connected=true;StatusChanged?.Invoke("Synthetic simulator connected; actuator commands disabled.");}
        public void Disconnect(string reason){lock(_sync){_connected=false;_state=null;}}
        public void Pump(CancellationToken token)
        {
            DateTime started=DateTime.UtcNow;
            while(!token.IsCancellationRequested)
            {
                lock(_sync)
                {
                    if(!_connected)return;
                    double t=(DateTime.UtcNow-started).TotalSeconds;
                    _state=new AircraftState{UtcTimestamp=DateTime.UtcNow,TelemetrySequence=++_samples,TelemetrySource="MOCK",AircraftTitle="Synthetic aircraft",SimTimeSeconds=t,LatitudeDeg=35.68+t*0.0001,LongitudeDeg=51.3,AltitudeMslFt=5000+200*Math.Sin(t/20),AltitudeAglFt=2000+200*Math.Sin(t/20),IndicatedAirspeedKts=100+5*Math.Sin(t/5),TrueAirspeedKts=110,GroundSpeedKts=105,HeadingTrueDeg=(90+t)%360,VerticalSpeedFpm=600*Math.Cos(t/20),WeightLb=2400,MaximumGrossWeightLb=2550,FuelFraction=0.7,DesignVs0Kts=45,DesignVs1Kts=50,EstimatedCruiseKts=110};
                }
                if(token.WaitHandle.WaitOne(40))return;
            }
        }
        public AircraftState GetLatestState(){lock(_sync)return _state;}
        public SimulatorTransportHealth GetHealth(){lock(_sync)return new SimulatorTransportHealth{Name=Name,IsConnected=_connected,HasTelemetry=_state!=null,LastTelemetryUtc=_state?.UtcTimestamp??DateTime.MinValue,TelemetrySamples=_samples,Capabilities=SimulatorCapabilities.Telemetry};}
        public void Send(ControlCommand command){throw new NotSupportedException("Synthetic telemetry mock has no flight dynamics or actuator authority.");}
        public void ResetScenario(){throw new NotSupportedException("Reset not supported.");}
        public void Dispose(){Disconnect("Disposed.");}
    }
}
