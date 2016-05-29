﻿//  Author:
//       Allis Tauri <allista@gmail.com>
//
//  Copyright (c) 2016 Allis Tauri
//
// This work is licensed under the Creative Commons Attribution-ShareAlike 4.0 International License. 
// To view a copy of this license, visit http://creativecommons.org/licenses/by-sa/4.0/ 
// or send a letter to Creative Commons, PO Box 1866, Mountain View, CA 94042, USA.
//
using System;
using UnityEngine;

namespace ThrottleControlledAvionics
{
	[CareerPart]
	[RequireModules(typeof(ManeuverAutopilot),
	                typeof(MatchVelocityAutopilot),
	                typeof(AttitudeControl),
	                typeof(ThrottleControl),
	                typeof(TranslationControl),
	                typeof(BearingControl),
	                typeof(TimeWarpControl))]
	public class RendezvouAutopilot : TargetedTrajectoryCalculator<RendezvousTrajectory, Vessel>
	{
		public new class Config : TCAModule.ModuleConfig
		{
			[Persistent] public float Dtol                = 100f;   //m
			[Persistent] public float StartOffset         = 60f;    //s
			[Persistent] public float MaxTTR              = 3f;     //1/VesselOrbit.period
			[Persistent] public float MaxDeltaV           = 100f;   //m/s
			[Persistent] public float CorrectionStart     = 10000f; //m
			[Persistent] public float CorrectionOffset    = 20f;    //s
			[Persistent] public float CorrectionTimer     = 10f;    //s
			[Persistent] public float ApproachThreshold   = 500f;   //m
			[Persistent] public float MaxApproachV        = 20f;    //parts
			[Persistent] public float ApproachVelF        = 0.01f;  //parts
			[Persistent] public float MaxInclinationDelta = 30;     //deg
			[Persistent] public float GTurnCurve          = 0.8f;
			[Persistent] public float LaunchTangentK      = 1f;
			[Persistent] public float Dist2VelF           = 0.1f;
		}
		static Config REN { get { return TCAScenario.Globals.REN; } }

		public RendezvouAutopilot(ModuleTCA tca) : base(tca) {}

		AttitudeControl ATC;
		ThrottleControl THR;
		BearingControl BRC;

		enum Stage { None, Start, Launch, ToOrbit, StartOrbit, ComputeRendezvou, Rendezvou, MatchOrbits, Approach, Brake }
		Stage stage;

		double CurrentDistance = -1;
		RendezvousTrajectory CurrentTrajectory 
		{ get { return new RendezvousTrajectory(VSL, Vector3d.zero, VSL.Physics.UT, Target, MinPeR); } }

		double ApAUT, TTA, MinAccelTime;
		double LaunchUT  = -1;
		double LaunchApR = -1;
		Vector3d TargetApV, ApV;
		SingleAction GearAction = new SingleAction();
		MinimumD MinDist = new MinimumD();
		ManeuverExecutor Executor;

		public override void Init()
		{
			base.Init();
			Dtol = REN.Dtol;
			CorrectionTimer.Period = REN.CorrectionTimer;
			CFG.AP2.AddHandler(this, Autopilot2.Rendezvou);
			GearAction.action = () => VSL.GearOn(false);
			Executor = new ManeuverExecutor(TCA);
		}

		public void RendezvouCallback(Multiplexer.Command cmd)
		{
			reset();
			switch(cmd)
			{
			case Multiplexer.Command.Resume:
			case Multiplexer.Command.On:
				if(!setup()) 
				{
					CFG.AP2.Off();
					return;
				}
				stage = Stage.Start;
				break;

			case Multiplexer.Command.Off:
				break;
			}
		}

		protected override bool check_target()
		{
			if(!base.check_target()) return false;
			var tVSL = VSL.TargetVessel;
			if(tVSL == null) 
			{
				Status("yellow", "Target should be a vessel");
				return false;
			}
			if(tVSL.LandedOrSplashed)
			{
				Status("yellow", "Target vessel is landed");
				return false;
			}
			if(Math.Abs(tVSL.orbit.inclination-VesselOrbit.inclination) > REN.MaxInclinationDelta)
			{
				Status("yellow", "Target orbit plane is tilted more than {0:F}° with respect to ours.\n" +
				       "You need to change orbit plane before the rendezvou maneuver.", REN.MaxInclinationDelta);
				return false;
			}
			return true;
		}

		protected override void setup_target()
		{ 
			Target = VSL.TargetVessel; 
			SetTarget(Target);
		}

		RendezvousTrajectory new_trajectory(double StartUT, double transfer_time)
		{
			var solver = new LambertSolver(VesselOrbit, Target.orbit.getRelativePositionAtUT(StartUT+transfer_time), StartUT);
			var dV = solver.dV4Transfer(transfer_time);
			return new RendezvousTrajectory(VSL, dV, StartUT, Target, MinPeR, transfer_time);
		}

		protected RendezvousTrajectory orbit_correction(RendezvousTrajectory old, RendezvousTrajectory best, ref double dT)
		{ return orbit_correction(old, best, VSL.Physics.UT + REN.CorrectionOffset, ref dT); }

		protected RendezvousTrajectory orbit_correction(RendezvousTrajectory old, RendezvousTrajectory best, double StartUT, ref double dT)
		{
			double transfer_time;
			if(VSL.Physics.UT+REN.CorrectionOffset > StartUT)
				StartUT = VSL.Physics.UT+REN.CorrectionOffset;
			if(old != null) 
			{
				transfer_time = old.TimeToTarget+dT;
				if(transfer_time > Target.orbit.period ||
				   transfer_time < REN.StartOffset ||
				   old.ManeuverDeltaV.sqrMagnitude > best.ManeuverDeltaV.sqrMagnitude &&
				   !old.KillerOrbit && !best.KillerOrbit)
				{
					dT /= -2.1;
					transfer_time = best.TimeToTarget+dT;
				}
			}
			else transfer_time = REN.StartOffset;
			return new_trajectory(StartUT, transfer_time);
		}

		bool trajectories_converging(RendezvousTrajectory cur, RendezvousTrajectory best)
		{
			return cur == best ||
				target_is_far(cur, best) || 
				Math.Abs(cur.ManeuverDeltaV.sqrMagnitude-best.ManeuverDeltaV.sqrMagnitude) > 1;
		}

		protected override void setup_calculation(NextTrajectory next)
		{
			next_trajectory = next;
			predicate = trajectories_converging;
		}
			
		void compute_rendezvou_trajectory()
		{
			Status("Performing rendezvous maneuver...");
			trajectory = null;
			stage = Stage.ComputeRendezvou;
			var transfer_time = VesselOrbit.period/4;
			var StartUT = VSL.Physics.UT+
				Utils.ClampL(TimeToResonance(VesselOrbit, Target.orbit, VSL.Physics.UT+REN.StartOffset)
				             *VesselOrbit.period-transfer_time, REN.StartOffset);
			double AtTargetUT;
			TrajectoryCalculator.ClosestApproach(VesselOrbit, Target.orbit, StartUT+REN.StartOffset, out AtTargetUT);
			StartUT = AtTargetUT-transfer_time;
			double dT = REN.CorrectionOffset;
			setup_calculation((o, b) => orbit_correction(o, b, StartUT, ref dT));
		}

		protected override void fine_tune_approach()
		{
			Status("Fine-tuning nearest approach...");
			trajectory = null;
			stage = Stage.ComputeRendezvou;
			double dT = REN.CorrectionOffset;
			setup_calculation((o, b) => orbit_correction(o, b, ref dT));
		}

		protected void compute_start_orbit(double StartUT)
		{
			Status("Achiving rendezvous orbit...");
			trajectory = null;
			stage = Stage.ComputeRendezvou;
			double dT = REN.CorrectionOffset;
			setup_calculation((o, b) => orbit_correction(o, b, StartUT, ref dT));
		}

		void start_orbit()
		{
			var dV = Vector3d.zero;
			var old = VesselOrbit;
			var StartUT = VSL.Physics.UT+REN.CorrectionOffset;
			if(VesselOrbit.PeR < MinPeR) 
			{
				update_trajectory();
				StartUT = Math.Min(trajectory.AtTargetUT, VSL.Physics.UT+(ApAhead? VesselOrbit.timeToAp : REN.CorrectionOffset));
				var transfer_time = Utils.ClampL(Target.orbit.period*(0.25-AngleDelta(VesselOrbit, Target.orbit, StartUT)/360), 1);
				var solver = new LambertSolver(VesselOrbit, Target.orbit.getRelativePositionAtUT(StartUT+transfer_time), StartUT);
				dV = solver.dV4Transfer(transfer_time);
				var trj = new RendezvousTrajectory(VSL, dV, StartUT, Target, MinPeR, transfer_time);
				LogF("StartT {}, transfer time {}\ndV {}\nPeR-MinPeR {}",
				     StartUT-VSL.Physics.UT, transfer_time, dV, trj.NewOrbit.PeR-MinPeR);//debug
				if(!dV.IsZero() && !trj.KillerOrbit)
				{
					compute_start_orbit(StartUT);
					return;
				}
				StartUT = ApAhead? VSL.Physics.UT+VesselOrbit.timeToAp : VSL.Physics.UT+REN.CorrectionOffset;
				dV = dV4C(old, hV(StartUT), StartUT);
				old = NewOrbit(old, dV, StartUT);
			}
			dV += dV4TTR(old, Target.orbit, REN.MaxTTR, REN.MaxDeltaV, MinPeR, StartUT);
			if(!dV.IsZero())
			{
				if(old == VesselOrbit) Status("Correcting orbit for resonance...");
				else Status("Achiving starting orbit...");
				add_node(dV, StartUT);
				CFG.AP1.On(Autopilot1.Maneuver);
			}
			stage = Stage.StartOrbit;
		}

		void to_orbit()
		{
			//sanity check
			if(VSL.Engines.NumActive > 0 && VSL.OnPlanet && VSL.OnPlanetParams.MaxTWR <= 1)
			{
				Status("red", "TWR < 1, impossible to achive orbit");
				CFG.AP2.Off();
				return;
			}
			//compute trajectory to get the needed apoapsis at needed time
			LaunchApR = Math.Max(MinPeR, (Target.orbit.PeR+Target.orbit.ApR)/2);
			var hVdir = Vector3d.Cross(Target.orbit.GetOrbitNormal(), VesselOrbit.pos).normalized;
			var LaunchRad = Utils.ClampH(Math.Atan(1/(Body.Radius*REN.LaunchTangentK/(2*LaunchApR) - 
			                                          Body.angularV/Math.Sqrt(2*VSL.Physics.StG*(LaunchApR-Body.Radius)))), 
			                             Utils.HalfPI);
			var velN = (Math.Sin(LaunchRad)*VesselOrbit.pos.normalized + Math.Cos(LaunchRad)*hVdir).normalized;
			var vel = Math.Sqrt(2*VSL.Physics.G*(LaunchApR-Body.Radius)) / Math.Sin(LaunchRad);
			var v   = 0.0;
			while(vel-v > TRJ.dVtol)
			{
				var V = (v+vel)/2;
				var o = NewOrbit(VesselOrbit, velN*V-VesselOrbit.vel, VSL.Physics.UT);
				if(o.ApR > LaunchApR) vel = V;
				else v = V;
			} vel = (v+vel)/2;
			TTA = AtmoSim.FromSurfaceTTA(VSL, LaunchApR-Body.Radius, REN.LaunchTangentK, REN.GTurnCurve);
			LaunchUT = VSL.Physics.UT;
			ApAUT = LaunchUT+TTA;
			var ascO = NewOrbit(VesselOrbit, velN*vel-VesselOrbit.vel, VSL.Physics.UT);
			if(VSL.LandedOrSplashed)
			{
				ApV = ascO.getRelativePositionAtUT(ApAUT);
				double TTR;
				do { TTR = correct_launch(); } 
				while(Math.Abs(TTR) > 1);
			}
			else TargetApV = ascO.getRelativePositionAtUT(ApAUT);
			LaunchApR = TargetApV.magnitude;
			CFG.DisableVSC();
			stage = VSL.LandedOrSplashed? Stage.Launch : Stage.ToOrbit;
		}

		double correct_launch()
		{
			var TTR = AngleDelta(Target.orbit, TargetApV, ApAUT)/360*Target.orbit.period;
			LaunchUT += TTR;
			if(LaunchUT-VSL.Physics.UT <= 0) 
				LaunchUT += Target.orbit.period;
			ApAUT= LaunchUT+AtmoSim.FromSurfaceTTA(VSL, LaunchApR-Body.Radius, REN.LaunchTangentK, REN.GTurnCurve);
			TargetApV = QuaternionD.AngleAxis((VSL.Physics.UT-LaunchUT)/Body.rotationPeriod*360, Body.angularVelocity.xzy)*
				ApV.normalized*Target.orbit.getRelativePositionAtUT(ApAUT).magnitude;
			LaunchApR = TargetApV.magnitude;
			return TTR;
		}

		void match_orbits()
		{
			SetTarget(Target);
			update_trajectory();
			add_target_node();
			CFG.AP1.On(Autopilot1.Maneuver);
			stage = Stage.MatchOrbits;
			MAN.MinDeltaV = 0.5f;
		}

		void approach()
		{
			CFG.AT.On(Attitude.Target);
			stage = Stage.Approach;
		}

		void brake()
		{
			SetTarget(Target);
			stage = Stage.Brake;
			var dV = VesselOrbit.vel-Target.orbit.vel;
			var dVm = dV.magnitude;
			var dist = Target.CurrentCoM-VSL.Physics.wCoM;
			var distm = dist.magnitude;
			if(distm < REN.Dtol && dVm < GLB.THR.MinDeltaV*2) return;
			if(distm > REN.Dtol && Vector3.Dot(dist, dV.xzy) > 0)
				CFG.AP1.On(Autopilot1.MatchVelNear);
			else CFG.AP1.On(Autopilot1.MatchVel);
		}

		void update_trajectory(bool update_distance=true)
		{
			if(trajectory == null) trajectory = CurrentTrajectory;
			else trajectory.UpdateOrbit(VesselOrbit);
			if(update_distance) CurrentDistance = trajectory.DistanceToTarget;
		}

		protected override void reset()
		{
			base.reset();
			stage = Stage.None;
			CurrentDistance = -1;
			CFG.AP1.Off();
		}

		protected override void UpdateState()
		{
			base.UpdateState();
			IsActive &= CFG.AP2[Autopilot2.Rendezvou];
			ControlsActive = IsActive || VSL.TargetVessel != null;
		}

		protected override void Update()
		{
			if(!IsActive) return;
			switch(stage)
			{
			case Stage.Start:
				if(VSL.InOrbit && 
				   VesselOrbit.ApR > MinPeR &&
				   VesselOrbit.radius > MinPeR)
				{
					update_trajectory();
					if(CurrentDistance > REN.CorrectionStart) start_orbit();
					else fine_tune_approach();
				}
				else to_orbit();
				break;
			case Stage.Launch:
				//correct launch time
				if(LaunchUT > VSL.Physics.UT) 
				{
					Status("Waiting for launch window...");
					correct_launch();
					VSL.Info.Countdown = LaunchUT-VSL.Physics.UT-MinAccelTime;
					WRP.WarpToTime = LaunchUT;
					break;
				}
				VSL.Engines.ActivateEnginesIfNeeded();
//				CSV(VSL.Physics.UT, VesselOrbit.radius-Body.Radius, VSL.VerticalSpeed.Absolute, 90);//debug
				if(VSL.VerticalSpeed.Absolute/VSL.Physics.G < 5)
				{ 
					Status("Liftoff...");
					CFG.VTOLAssistON = true;
					THR.Throttle = 1; 
					break; 
				}
				GearAction.Run();
				CFG.VTOLAssistON = false;
				CFG.StabilizeFlight = false;
				CFG.HF.Off();
				stage = Stage.ToOrbit;
				MinDist.Reset();
				break;
			case Stage.ToOrbit:
				var dApA   = LaunchApR-VesselOrbit.ApR;
				var vel    = Vector3d.zero;
				var in_atm = Body.atmosphere && VesselOrbit.radius < Body.Radius+Body.atmosphereDepth;
				if(dApA < REN.Dtol)
				{
					update_trajectory();
					MinDist.Update(CurrentDistance);
					if(MinDist) { start_orbit(); break; }
				}
				ApAUT = VSL.Physics.UT+VesselOrbit.timeToAp;
				var cApV   = VesselOrbit.getRelativePositionAtUT(ApAUT);
				var hv     = Vector3d.Exclude(VesselOrbit.pos, VesselOrbit.vel).normalized;
				var alpha  = Utils.ProjectionAngle(cApV, TargetApV, Vector3d.Cross(VesselOrbit.GetOrbitNormal(), cApV))*Mathf.Deg2Rad*Body.Radius;
				if(alpha > REN.Dtol)
				{
					var hvel = Utils.ClampL(alpha-dApA*REN.GTurnCurve, 0)*REN.Dist2VelF*
						Utils.Clamp((VesselOrbit.ApA-VSL.Altitude.Absolute)/100, 0, 1);
					if(Body.atmosphere) hvel *= Math.Sqrt(Utils.Clamp(VSL.Altitude.Absolute/Body.atmosphereDepth, 0, 1));
					vel += hv*hvel;
				}
				if(dApA > REN.Dtol)
					vel += VSL.Physics.Up.xzy*dApA*REN.Dist2VelF;
				vel *= VSL.Physics.StG/Utils.G0;
				var norm = VesselOrbit.GetOrbitNormal();
				vel += norm*Math.Sin((90-Vector3d.Angle(norm, TargetApV))*Mathf.Deg2Rad)*vel.magnitude*10;
				vel = vel.xzy;
//				LogF("TimeToTarget {}, dApA {}, alpha {}, vel {}, dFi {}",
//				     ApAUT-VSL.Physics.UT, 
//				     dApA, alpha, vel.magnitude, 
//				     90-Vector3d.Angle(VesselOrbit.GetOrbitNormal(), TargetApV));//debug
				if(CFG.AT.Not(Attitude.KillRotation)) 
					BRC.ForwardDirection = hV(VSL.Physics.UT).xzy;
				if(Executor.Execute(vel, 1)) 
				{
					Status("Gravity turn...");
					break;
				}
				Status("Coasting...");
				CFG.AT.OnIfNot(Attitude.KillRotation);
				THR.Throttle = 0;
				if(in_atm) break;
				LaunchUT = -1;
				start_orbit();
				break;
			case Stage.StartOrbit:
//				CSV(VSL.Physics.UT, VesselOrbit.radius-Body.Radius, VSL.VerticalSpeed.Absolute, 0);//debug
				if(CFG.AP1[Autopilot1.Maneuver]) break;
				CurrentDistance = -1;
				update_trajectory(false);
				if(trajectory.DistanceToTarget < REN.CorrectionStart ||
				   (trajectory.TimeToTarget+trajectory.TimeToStart)/VesselOrbit.period > REN.MaxTTR) 
					stage = Stage.Rendezvou;
				else compute_rendezvou_trajectory();
				break;
			case Stage.ComputeRendezvou:
				if(!trajectory_computed()) break;
				if(trajectory.ManeuverDeltaV.magnitude > GLB.THR.MinDeltaV*5 &&
				   trajectory.DistanceToTarget < REN.CorrectionStart &&
				   (CurrentDistance < 0 || trajectory.DistanceToTarget < CurrentDistance))
				{
					CorrectionTimer.Start();
					CurrentDistance = trajectory.DistanceToTarget;
					add_trajectory_node();
					CFG.AP1.On(Autopilot1.Maneuver);
					stage = Stage.Rendezvou;
				}
				else if(trajectory.DistanceToTarget < REN.CorrectionStart) match_orbits();
				else 
				{
					Status("red", "Failed to compute rendezvou trajectory.\nPlease, try again.");
					CFG.AP2.Off();
					return;
				}
				break;
			case Stage.Rendezvou:
				if(CFG.AP1[Autopilot1.Maneuver]) break;
				if(!CorrectionTimer.Check && CurrentDistance >= 0) break;
				CorrectionTimer.Reset();
				update_trajectory();
				if(CurrentDistance < REN.ApproachThreshold ||
				   CurrentDistance < REN.CorrectionStart/2 && 
				   trajectory.TimeToTarget < VSL.Engines.TTB((float)trajectory.BrakeDeltaV.magnitude)+REN.StartOffset)
					match_orbits();
				else fine_tune_approach();
				break;
			case Stage.MatchOrbits:
				Status("Matching orbits at nearest approach...");
				if(CFG.AP1[Autopilot1.Maneuver]) break;
				update_trajectory();
				var dist = (Target.orbit.pos-VesselOrbit.pos).magnitude;
				if(dist > REN.CorrectionStart) start_orbit();
				else if(dist > REN.CorrectionStart/4) fine_tune_approach();
				else if(dist > REN.Dtol) approach();
				else brake();
				break;
			case Stage.Approach:
				Status("Approaching...");
				var dP = Target.orbit.pos-VesselOrbit.pos;
				var dPm = dP.magnitude;
				if(dPm - VSL.Geometry.R < REN.Dtol) 
				{ brake(); break; }
				if(ATC.AttitudeError > 1) break;
				var dV = Vector3d.Dot(VesselOrbit.vel-Target.orbit.vel, dP/dPm);
				var nV = Utils.Clamp(dPm*REN.ApproachVelF, 1, REN.MaxApproachV);
				if(dV+GLB.THR.MinDeltaV < nV) THR.DeltaV = (float)(nV-dV);
				else brake();
				break;
			case Stage.Brake:
				Status("Braking near target...");
				if(CFG.AP1[Autopilot1.MatchVelNear]) break;
				if(CFG.AP1[Autopilot1.MatchVel])
				{
					if((Target.orbit.vel-VesselOrbit.vel).magnitude > GLB.THR.MinDeltaV) break;
					CFG.AP1.Off();
					THR.Throttle = 0;
				}
				if((VSL.Physics.wCoM-Target.CurrentCoM).magnitude-VSL.Geometry.R > REN.Dtol)
				{ approach(); break; }
				CFG.AP2.Off();
				CFG.AT.OnIfNot(Attitude.KillRotation);
				break;
			}
		}

		public override void Draw()
		{
			#if DEBUG
			if(Target != null && Body != null && Target.orbit != null)
			{
				GLUtils.GLVec(Body.position, TargetApV.xzy, Color.green);//debug
				GLUtils.GLVec(Body.position, Target.orbit.getRelativePositionAtUT(ApAUT).xzy, Color.magenta);//debug
			}
			#endif
			if(ControlsActive)
			{
				if(computing) 
				{
					if(GUILayout.Button("Computing...", Styles.inactive_button, GUILayout.ExpandWidth(false)))
						CFG.AP2.Off();
//					#if DEBUG
//					if(current != null)
//					{
//						GLUtils.GLVec(Body.position, current.AtTargetPos.xzy, Color.green);//debug
//						GLUtils.GLVec(Body.position, current.TargetPos.xzy, Color.magenta);//debug
//						GLUtils.GLVec(Body.position+current.StartPos.xzy, current.ManeuverDeltaV.normalized.xzy*Body.Radius/4, Color.yellow);//debug
//						GLUtils.GLLine(Body.position+current.StartPos.xzy, Body.position+current.TargetPos.xzy, Color.cyan);//debug
//					}
//					#endif
				}
				else if(Utils.ButtonSwitch("Rendezvou", CFG.AP2[Autopilot2.Rendezvou],
				                           "Compute and perform a rendezvou maneuver, then brake near the target.", 
				                           GUILayout.ExpandWidth(false)))
					CFG.AP2.XToggle(Autopilot2.Rendezvou);
			}
			else GUILayout.Label(new GUIContent("Rendezvou", "Compute and perform a rendezvou maneuver, then brake near the target."), 
			                     Styles.inactive_button, GUILayout.ExpandWidth(false));
		}

//		#if DEBUG
//		public void RadarBeam()
//		{
//			if(VSL == null || VSL.vessel == null || VSL.refT == null || !CFG.AP2[Autopilot2.Rendezvou]) return;
//			if(stage == Stage.ToOrbit)
//			{
//				GLUtils.GLVec(VSL.Physics.wCoM, tNorm.normalized.xzy*12, Color.green);
//				GLUtils.GLVec(VSL.Physics.wCoM, norm.normalized.xzy*10, Color.yellow);
//				GLUtils.GLVec(VSL.Physics.wCoM, hVdir.normalized.xzy*10, Color.red);
//				GLUtils.GLVec(VSL.Physics.wCoM, VesselOrbit.pos.normalized.xzy*10, Color.blue);
//				GLUtils.GLVec(VSL.Physics.wCoM, velN.xzy*10, Color.cyan);
//				GLUtils.GLVec(VSL.Physics.wCoM, -Body.angularVelocity.normalized*10, Color.white);
//				GLUtils.GLVec(VSL.Physics.wCoM, TargetApV.normalized.xzy*10, Color.magenta);
//			}
//		}
//		#endif
	}
}

