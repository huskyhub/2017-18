﻿using System;
using Scarlet.Components;
using Scarlet.Components.Motors;
using Scarlet.Components.Sensors;

namespace Science.Systems
{
    public class Toolhead : ISubsystem
    {
        private const float MOTOR_MAX_SPEED = 0.05F;

        private int Angle;

        private readonly TalonMC MotorCtrl;
        private readonly Potentiometer Pot;

        public Toolhead()
        {
            // TODO: Set these to actual pins.
            this.MotorCtrl = new TalonMC(null, MOTOR_MAX_SPEED);
            this.Pot = new Potentiometer(null, 300);

            this.Pot.Turned += this.EventTriggered;
        }

        public void EmergencyStop()
        {
            this.MotorCtrl.Stop();
            this.UpdateState();
        }

        public void EventTriggered(object Sender, EventArgs Event)
        {
            if(Event is PotentiometerTurn)
            {
                // TODO: Track this.
            }
        }

        public void Initialize()
        {
            this.GotoDrill();
        }

        public void GotoMoisture()
        {
            // TODO: Implement.
        }

        public void GotoDrill()
        {
            // TODO: Implement.
        }

        public void GotoThermo()
        {
            // TODO: Implement.
        }

        public void UpdateState()
        {
            this.Pot.UpdateState();
            // TODO: Calculate and set motor speed.
            this.MotorCtrl.UpdateState();
        }
    }
}
