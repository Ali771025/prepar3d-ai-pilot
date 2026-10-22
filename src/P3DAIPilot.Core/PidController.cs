using System;

namespace P3DAIPilot.Core
{
    public sealed class PidController
    {
        private readonly double _kp;
        private readonly double _ki;
        private readonly double _kd;
        private readonly double _minOutput;
        private readonly double _maxOutput;
        private readonly double _derivativeAlpha;
        private double _integral;
        private double _previousError;
        private double _filteredDerivative;
        private bool _initialized;

        public PidController(double kp, double ki, double kd, double minOutput, double maxOutput, double derivativeAlpha = 0.25)
        {
            if (minOutput >= maxOutput) throw new ArgumentException("PID output limits are invalid.");
            _kp = kp;
            _ki = ki;
            _kd = kd;
            _minOutput = minOutput;
            _maxOutput = maxOutput;
            _derivativeAlpha = MathEx.Clamp(derivativeAlpha, 0.0, 1.0);
        }

        public double Update(double error, double dtSeconds)
        {
            if (dtSeconds <= 0.0 || dtSeconds > 1.0 || !MathEx.AreFinite(error, dtSeconds)) return 0.0;

            double derivative = _initialized ? (error - _previousError) / dtSeconds : 0.0;
            _filteredDerivative += _derivativeAlpha * (derivative - _filteredDerivative);
            double candidateIntegral = _integral + error * dtSeconds;
            double unclamped = _kp * error + _ki * candidateIntegral + _kd * _filteredDerivative;
            double output = MathEx.Clamp(unclamped, _minOutput, _maxOutput);

            bool saturatedHigh = unclamped > _maxOutput && error > 0.0;
            bool saturatedLow = unclamped < _minOutput && error < 0.0;
            if (!saturatedHigh && !saturatedLow) _integral = candidateIntegral;

            _previousError = error;
            _initialized = true;
            return output;
        }

        public void Reset()
        {
            _integral = 0.0;
            _previousError = 0.0;
            _filteredDerivative = 0.0;
            _initialized = false;
        }
    }
}
