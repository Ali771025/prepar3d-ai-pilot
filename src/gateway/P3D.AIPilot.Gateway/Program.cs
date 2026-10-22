using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using P3DAIPilot.Core;
using P3DAIPilot.Bridge;
using P3DAIPilot.P3Dv4;
internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        try {return await Run(args);} catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    static async Task<int> Run(string[] args)
    {
        string configPath=ConfigurationLoader.ResolvePath(args);
        var config=ConfigurationLoader.Load(configPath);
        var profiles=AircraftProfileCatalog.Load(config.AircraftProfiles,Path.GetDirectoryName(configPath));
        string forced=ConfigurationLoader.GetOptionValue(args,"--aircraft-profile");
        if(forced!=null){var selected=profiles.Resolve(null,forced,out _);if(selected==null)throw new InvalidDataException("No aircraft profile available.");selected.ApplyTo(config);}
        var errors=config.Validate();if(errors.Count>0)throw new InvalidDataException(string.Join("; ",errors));
        string pipe=ConfigurationLoader.GetOptionValue(args,"--pipe")??"P3D-AIPilot-Telemetry";
        using var stop=new CancellationTokenSource();
        Console.CancelKeyPress+=(s,e)=>{e.Cancel=true;stop.Cancel();};
        string duration=ConfigurationLoader.GetOptionValue(args,"--duration-seconds");
        if(duration!=null){if(!int.TryParse(duration,out int seconds)||seconds<1||seconds>86400)throw new InvalidDataException("Invalid duration.");stop.CancelAfter(TimeSpan.FromSeconds(seconds));}
        using ISimulatorTransport adapter=ConfigurationLoader.HasOption(args,"--mock")?new MockSimulatorAdapter():new PdkAdapter(pipe,requireOwnedTarget:ConfigurationLoader.HasOption(args,"--mission-ai"));
        var pdk=adapter as PdkAdapter;
        bool missionAi=ConfigurationLoader.HasOption(args,"--mission-ai");
        if(missionAi&&(config.TakeoffMission==null||!config.TakeoffMission.Enabled))throw new InvalidDataException("Mission AI requires TakeoffMission.Enabled.");
        using var humanAdapter=missionAi?new PdkAdapter("P3D-AIPilot-Human"):null;
        using var humanSupervisor=humanAdapter!=null?new ConnectionSupervisor(humanAdapter):null;
        humanSupervisor?.Start();
        using var humanRecorder=missionAi?new FlightCsvRecorder("data/flights/human"):null;
        DateTime humanRecorded=DateTime.MinValue;uint observedObjectId=0;
        using var supervisor=new ConnectionSupervisor(adapter);
        using var llm=new LocalLlmSupervisor(config.Llm);
        using var rl=new RlBridgeServer(config.Rl);
        if(missionAi)config.Safety.ManualOverrideEnabled=false; // Human joystick belongs to the user object, not this AI.
        var engine=new AutopilotEngine(config);RunwayFrame runway=null;
        FlightCsvRecorder recorder=(config.RecordOnStart||ConfigurationLoader.HasOption(args,"--record"))?new FlightCsvRecorder("data/flights"):null;DateTime recorded=DateTime.MinValue;
        engine.GuidanceOverride=llm.ApplyOverride;engine.ControlOverride=rl.ApplyResidual;
        supervisor.StatusChanged+=Console.WriteLine;adapter.StatusChanged+=Console.WriteLine;
        llm.StatusChanged+=Console.WriteLine;
        rl.Start();supervisor.Start();
        Console.WriteLine("PDK Gateway: AI OFF. C=capture runway, R=record, A=activate, D/F12=release, Q=quit. Reconnection requires explicit A.");
        DateTime dashboard=DateTime.MinValue;
        string observedTitle=null;bool profileBlocked=profiles.RequireMatch&&forced==null;
        var clock=Stopwatch.StartNew();long previousTicks=clock.ElapsedTicks;
        DateTime recordingSession=DateTime.MinValue;
        void Release(string reason,bool preserveTerminal=false)
        {
            if(!preserveTerminal||engine.IsArmed)engine.Disarm(reason);
            try{pdk?.ReleaseControl();}catch(Exception ex){Console.Error.WriteLine("Release failed; disconnecting: "+ex.Message);adapter.Disconnect(reason);}
        }
        try
        {
            while(!stop.IsCancellationRequested)
            {
                long ticks=clock.ElapsedTicks;double dt=MathEx.Clamp((ticks-previousTicks)/(double)Stopwatch.Frequency,0.005,0.20);previousTicks=ticks;
                var state=adapter.GetLatestState();var health=adapter.GetHealth();
                var human=humanAdapter?.GetLatestState();
                if(human!=null&&human.UtcTimestamp!=humanRecorded&&humanAdapter.GetHealth().IsFresh(DateTime.UtcNow,config.TelemetryTimeoutSeconds)){
                    human.ControlRole="human";
                    humanRecorder.Append(human,new AutopilotOutput{Phase=FlightPhase.Disarmed,ApplyControls=false},null,null);humanRecorded=human.UtcTimestamp;
                }
                if(missionAi&&state!=null){
                    state.ControlRole="ai";
                    if(observedObjectId!=0&&state.AircraftObjectId!=observedObjectId){Release("AI object changed; recapture runway.");runway=null;}
                    observedObjectId=state.AircraftObjectId;
                }
                if(state!=null&&profiles.Enabled&&forced==null&&state.AircraftTitle!=observedTitle)
                {
                    Release("Aircraft changed; profile checks required.");runway=null;observedTitle=state.AircraftTitle;
                    var selected=profiles.Resolve(observedTitle,null,out _);profileBlocked=selected==null;
                    if(selected!=null){selected.ApplyTo(config);if(missionAi)config.Safety.ManualOverrideEnabled=false;engine=new AutopilotEngine(config);engine.GuidanceOverride=llm.ApplyOverride;engine.ControlOverride=rl.ApplyResidual;}
                    Console.WriteLine(profileBlocked?"Aircraft profile blocked: "+observedTitle:"Aircraft profile: "+selected.Id);
                }
                if(recorder!=null&&recordingSession!=health.ConnectedUtc)
                {recorder.Dispose();recorder=new FlightCsvRecorder("data/flights");recorded=DateTime.MinValue;recordingSession=health.ConnectedUtc;}
                bool ready=state!=null&&state.IsFinite()&&health.IsFresh(DateTime.UtcNow,config.TelemetryTimeoutSeconds)&&!state.Paused&&!state.SlewActive&&(!missionAi||(pdk!=null&&pdk.IsOwnedTarget&&state.AircraftObjectId!=0));
                if(rl.ConsumeResetRequest()){Release("PDK reset unavailable.");rl.CompleteReset(false,"This adapter does not support scenario reset.");}
                if(engine.IsArmed&&(!ready||!(pdk?.HasAuthority??false)))Release("Telemetry/authority lost; re-arm required.");
                if(!Console.IsInputRedirected&&Console.KeyAvailable)
                {
                    var key=Console.ReadKey(true).Key;
                    if(key==ConsoleKey.Q){stop.Cancel();break;}
                    if(key==ConsoleKey.D||key==ConsoleKey.F12)Release("Manual emergency release.");
                    if(key==ConsoleKey.C&&ready&&state.OnGround)runway=new RunwayFrame{ThresholdLatitudeDeg=state.LatitudeDeg,ThresholdLongitudeDeg=state.LongitudeDeg,ThresholdElevationFt=state.AltitudeMslFt-state.AltitudeAglFt,HeadingTrueDeg=state.HeadingTrueDeg,LengthM=missionAi?3500:2000,WidthM=45};
                    if(key==ConsoleKey.R){if(recorder!=null){recorder.Dispose();recorder=null;}else{recorder=new FlightCsvRecorder("data/flights");Console.WriteLine(recorder.FilePath);}}
                    if(key==ConsoleKey.A&&ready&&!profileBlocked)
                    {
                        if(engine.Arm(state,runway,out string reason))
                        {try{(pdk??throw new InvalidOperationException("Mock telemetry cannot acquire flight controls.")).AcquireControl();rl.CompleteReset(true,"Manual activation started a new episode.");}catch(Exception ex){Release("Acquire rejected.");Console.WriteLine(ex.Message);}}
                        Console.WriteLine(reason);
                    }
                }
                if(ready)
                {
                    engine.ApplySupervisorIntent(llm.GetValidatedIntent(engine.Phase));
                    var output=engine.Update(state,dt,DateTime.UtcNow); // Existing deterministic controller and safety shield.
                    if(output.Targets!=null)llm.Observe(output.Phase,state,output.Targets);
                    if(output.ApplyControls&&(pdk?.HasAuthority??false))
                    {try{adapter.Send(output.Command);}catch(Exception ex){Release("Command failed.");Console.WriteLine(ex.Message);}}
                    else if((pdk?.HasAuthority??false))Release("Controller stopped applying controls.",preserveTerminal:true);
                    rl.Publish(state,output);
                    if(recorder!=null&&recorded!=state.UtcTimestamp){recorder.Append(state,output,llm.LastDecision,rl.LastTrace);recorded=state.UtcTimestamp;}
                }
                if((DateTime.UtcNow-dashboard).TotalSeconds>=1)
                {
                    Console.WriteLine($"{supervisor.State} authority={(pdk?.HasAuthority??false)} AI={engine.Phase} alt={state?.AltitudeMslFt:F0} IAS={state?.IndicatedAirspeedKts:F1} hdg={state?.HeadingTrueDeg:F1} samples={health.TelemetrySamples} retries={health.ReconnectAttempts} error={health.LastError}");dashboard=DateTime.UtcNow;
                }
                await Task.Delay(40,stop.Token);
            }
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested){}
        finally{Release("Gateway shutdown.");recorder?.Dispose();}
        return 0;
    }
}
