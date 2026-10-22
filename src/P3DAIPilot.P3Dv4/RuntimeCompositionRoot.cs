using System.Collections.Generic;
using P3DAIPilot.Core;

namespace P3DAIPilot.P3Dv4
{
    internal static class RuntimeCompositionRoot
    {
        public static SimulatorGateway CreateSimulatorGateway(AppConfiguration configuration)
        {
            var transports = new List<ISimulatorTransport>();
            if (configuration.Simulator.Allows("SimConnect"))
                transports.Add(new P3DSimConnectClient());
            if (configuration.Simulator.Fsuipc.Enabled && configuration.Simulator.Allows("FSUIPC"))
                transports.Add(new FsuipcSimulatorClient(configuration.Simulator.Fsuipc));
            return new SimulatorGateway(configuration.Simulator, transports);
        }
    }
}
