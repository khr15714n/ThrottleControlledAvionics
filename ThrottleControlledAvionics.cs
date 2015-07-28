/* Name: Throttle Controlled Avionics, Fork by Allis Tauri
 *
 * Authors: Quinten Feys & Willem van Vliet & Allis Tauri
 * License: BY: Attribution-ShareAlike 3.0 Unported (CC BY-SA 3.0): 
 * http://creativecommons.org/licenses/by-sa/3.0/
 * 
 */

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ThrottleControlledAvionics
{
	[KSPAddon(KSPAddon.Startup.Flight, false)]
	public class ThrottleControlledAvionics : MonoBehaviour
	{
		const string TCA_PART = "ThrottleControlledAvionics";

		#region State
		TCAGui GUI;
		Vessel vessel;
		public VesselConfig CFG { get; private set; }
		public readonly List<EngineWrapper> Engines = new List<EngineWrapper>();
		public readonly List<ModuleReactionWheel> RWheels = new List<ModuleReactionWheel>();
		public float TorqueError { get; private set; }
		public float TorqueAngle { get; private set; }
		public TCAState State { get; private set; }
		public bool OnPlanet { get { return vessel.OnPlanet(); } }
		//career and technical availability
		public bool Available { get; private set; }
		public bool Controllable { get { return Available && vessel.IsControllable; } }
		//physics
		Vector3d up;      //up unit vector in world space
		Vector3 wCoM;     //center of mass in world space
		Transform refT;   //transform of the controller-part
		Vector3 steering; //previous steering vector
		Vector3 torque;   //current torque applied to the vessel (not including aerodynamic forces)
		Vector6 e_torque_limits; //torque limits of engines
		Vector6 r_torque_limits; //torque limits of reaction wheels
		bool    normilize_limits;
		float   maxTWR, accel_speed, decel_speed;
		Matrix3x3f inertiaTensor;
		Vector3 MoI = Vector3.one; //main diagonal of inertia tensor
		Vector3 angularA = Vector3.zero; //current angular acceleration
		PIv_Controller   angularA_filter     = new PIv_Controller();
		PIDv_Controller  hV_controller       = new PIDv_Controller();
		PIDf_Controller2 alt_controller      = new PIDf_Controller2();
		PIDf_Controller  jets_alt_controller = new PIDf_Controller();

		public float VerticalSpeedFactor { get; private set; } = 1f;
		public float VerticalSpeed { get; private set; }
		public float VerticalAccel { get; private set; }
		public float Altitude { get; private set; }
		public bool  IsStateSet(TCAState s) { return (State & s) == s; }

		double terrain_altitude
		{ get { return (vessel.mainBody.ocean && vessel.terrainAltitude < 0)? 0 : vessel.terrainAltitude; } }
		float current_altitude
		{ get { return CFG.AltitudeAboveTerrain ? (float)(vessel.altitude - terrain_altitude) : (float)vessel.altitude; } }
		#endregion

		#region Initialization
		#if DEBUG
		public void OnReloadGlobals()
		{
			angularA_filter.setPI(TCAConfiguration.Globals.AngularA);
			hV_controller.P = TCAConfiguration.Globals.HvP;
			alt_controller.setPID(TCAConfiguration.Globals.AltitudeController);
			jets_alt_controller.setPID(TCAConfiguration.Globals.JetsAltitudeController);
		}
		#endif

		public void Awake()
		{
			TCAConfiguration.Load();
			angularA_filter.setPI(TCAConfiguration.Globals.AngularA);
			hV_controller.P = TCAConfiguration.Globals.HvP;
			alt_controller.setPID(TCAConfiguration.Globals.AltitudeController);
			jets_alt_controller.setPID(TCAConfiguration.Globals.JetsAltitudeController);
			GameEvents.onVesselChange.Add(onVesselChange);
			GameEvents.onVesselWasModified.Add(onVesselModify);
			GameEvents.onGameStateSave.Add(onSave);
		}

		internal void OnDestroy() 
		{ 
			TCAConfiguration.Save();
			if(GUI != null) GUI.OnDestroy();
			GameEvents.onVesselChange.Remove(onVesselChange);
			GameEvents.onVesselWasModified.Remove(onVesselModify);
			GameEvents.onGameStateSave.Remove(onSave);
		}

		void onVesselChange(Vessel vsl)
		{ 
			if(vsl == null || vsl.Parts == null) return;
			save(); reset();
			vessel = vsl;
			init();
		}

		void onVesselModify(Vessel vsl)
		{ if(vessel == vsl) init(); }

		void onSave(ConfigNode node) { save(); }

		void save() 
		{ 
			TCAConfiguration.Save(); 
			if(GUI != null) GUI.SaveConfig();
		}

		void reset()
		{
			if(vessel != null) 
			{
				vessel.OnAutopilotUpdate -= block_throttle;
				vessel.OnAutopilotUpdate -= kill_horizontal_velocity;
			}
			vessel = null; 
			CFG = null;
			Engines.Clear();
			angularA_filter.Reset();
			Available = false;
		}

		void init()
		{
			Available = false;
			vessel.OnAutopilotUpdate += block_throttle;
			vessel.OnAutopilotUpdate += kill_horizontal_velocity;
			if(!vessel.isEVA && 
			   (!TCAConfiguration.Globals.IntegrateIntoCareer ||
			    Utils.PartIsPurchased(TCA_PART)))
			{
				updateVessel();
				if(Engines.Count > 0)
				{
					if(GUI == null) GUI = new TCAGui(this);
					Utils.Log("TCA is enabled");//debug
					Available = true;
					return;
				}
			} 
			if(GUI != null) { GUI.OnDestroy(); GUI = null; }
			Utils.Log("TCA is disabled.\nVessel is EVA: {0}; TCA available in TechTree: {1}; Engines count: {2}",
			          vessel.isEVA, 
			          (!TCAConfiguration.Globals.IntegrateIntoCareer ||
			            Utils.PartIsPurchased(TCA_PART)),
			          Engines.Count);//debug
		}

		void updateVessel()
		{
			if(vessel == null) return;
			CFG = TCAConfiguration.GetConfig(vessel);
			EngineWrapper.ThrustPI.setMaster(CFG.Engines);
			Engines.Clear();
			foreach(Part p in vessel.Parts)
				foreach(var module in p.Modules)
				{	
					var engine = module as ModuleEngines;
					if(engine != null) Engines.Add(new EngineWrapper(engine));
					var rwheel = module as ModuleReactionWheel;
					if(rwheel != null) RWheels.Add(rwheel);
				}
		}
		#endregion

		#region Controls
		public void ActivateTCA(bool state)
		{
			if(state == CFG.Enabled) return;
			CFG.Enabled = state;
			if(!CFG.Enabled) //reset engine limiters
			{
				Engines.ForEach(e => e.forceThrustPercentage(100));
				State = TCAState.Disabled;
			}
		}
		public void ToggleTCA() { ActivateTCA(!CFG.Enabled); }

		public void BlockThrottle(bool state)
		{
			if(state == CFG.BlockThrottle) return;
			CFG.BlockThrottle = state;
			if(CFG.BlockThrottle && !CFG.VerticalSpeedControl)
				CFG.VerticalCutoff = 0;
		}

		public void KillHorizontalVelocity(bool state)
		{
			if(state == CFG.KillHorVel) return;
			CFG.KillHorVel = state;
			if(CFG.KillHorVel)
				CFG.SASWasEnabled = vessel.ActionGroups[KSPActionGroup.SAS];
			else if(CFG.SASWasEnabled)
				vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
		}
		public void ToggleHvAutopilot() { KillHorizontalVelocity(!CFG.KillHorVel); }

		public void MaintainAltitude(bool state)
		{
			if(state == CFG.ControlAltitude) return;
			CFG.ControlAltitude = state;
			if(CFG.ControlAltitude)
			{
				Altitude = current_altitude;
				CFG.DesiredAltitude = Altitude;
			}
		}
		public void ToggleAltitudeAutopilot() { MaintainAltitude(!CFG.ControlAltitude); }

		public void AltitudeAboveTerrain(bool state)
		{
			if(state == CFG.AltitudeAboveTerrain) return;
			CFG.AltitudeAboveTerrain = state;
			Altitude = current_altitude;
			if(CFG.AltitudeAboveTerrain)
				CFG.DesiredAltitude -= (float)terrain_altitude;
			else CFG.DesiredAltitude += (float)terrain_altitude;
		}
		public void ToggleAltitudeAboveTerrain() { AltitudeAboveTerrain(!CFG.AltitudeAboveTerrain);}
		#endregion

		#region Main Logic
		public void OnGUI() 
		{ 
			if(!Available) return;
			Styles.Init();
			if(Controllable) GUI.DrawGUI(); 
			TCAToolbarManager.UpdateToolbarButton();
		}

		public void Update()
		{ 
			if(!Controllable) return;
			GUI.OnUpdate();
			if(CFG.Enabled && CFG.BlockThrottle)
			{
				if(CFG.ControlAltitude)
				{
					if(GameSettings.THROTTLE_UP.GetKey())
						CFG.DesiredAltitude = Mathf.Lerp(CFG.DesiredAltitude, 
						                                 CFG.DesiredAltitude+10, 
						                                 CFG.VSControlSensitivity);
					else if(GameSettings.THROTTLE_DOWN.GetKey())
						CFG.DesiredAltitude = Mathf.Lerp(CFG.DesiredAltitude,
						                                 CFG.DesiredAltitude-10, 
						                                 CFG.VSControlSensitivity);
					else if(GameSettings.THROTTLE_FULL.GetKeyDown())
						CFG.DesiredAltitude = CFG.DesiredAltitude+10;
					else if(GameSettings.THROTTLE_CUTOFF.GetKeyDown())
						CFG.DesiredAltitude = CFG.DesiredAltitude-10;
				}
				else
				{
					if(GameSettings.THROTTLE_UP.GetKey())
						CFG.VerticalCutoff = Mathf.Lerp(CFG.VerticalCutoff, 
						                                TCAConfiguration.Globals.MaxVS, 
						                                CFG.VSControlSensitivity);
					else if(GameSettings.THROTTLE_DOWN.GetKey())
						CFG.VerticalCutoff = Mathf.Lerp(CFG.VerticalCutoff, 
						                                -TCAConfiguration.Globals.MaxVS, 
						                                CFG.VSControlSensitivity);
					else if(GameSettings.THROTTLE_FULL.GetKeyDown())
						CFG.VerticalCutoff = TCAConfiguration.Globals.MaxVS;
					else if(GameSettings.THROTTLE_CUTOFF.GetKeyDown())
						CFG.VerticalCutoff = -TCAConfiguration.Globals.MaxVS;
				}
			}
		}

		public void FixedUpdate()
		{
			if(!Available || !CFG.Enabled) return;
			State = TCAState.Enabled;
			//check for throttle and Electrich Charge
			if(vessel.ctrlState.mainThrottle <= 0) return;
			State |= TCAState.Throttled;
			if(!vessel.ElectricChargeAvailible()) return;
			State |= TCAState.HaveEC;
			//update engines if needed
			if(Engines.Any(e => !e.Valid)) updateVessel();
			var active_engines = Engines.Where(e => e.isOperational).ToList();
			var num_active = active_engines.Count;
			if(num_active == 0) return;
			State |= TCAState.HaveActiveEngines;
			//update physical parameters
			wCoM = vessel.CoM + vessel.rb_velocity*TimeWarp.fixedDeltaTime;
			refT = vessel.ReferenceTransform;
			up   = (wCoM - vessel.mainBody.position).normalized;
			if(CFG.AutoTune || CFG.KillHorVel) updateMoI();
			//initialize control state
			update_rwheels();
			update_vessel_stats(active_engines);
			update_VerticalCutoff();
			update_VerticalSpeedFactor(active_engines);
			initialize_engines(active_engines);
			//sort active engines
			var balanced_engines = new List<EngineWrapper>(num_active);
			var steering_engines = new List<EngineWrapper>(num_active);
			var maneuver_engines = new List<EngineWrapper>(num_active);
			var manual_engines   = new List<EngineWrapper>(num_active); 
			for(int i = 0; i < num_active; i++)
			{
				var e = active_engines[i];
				switch(e.Role)
				{
				case TCARole.MAIN:
					steering_engines.Add(e);
					break;
				case TCARole.MANEUVER:
					steering_engines.Add(e);
					maneuver_engines.Add(e);
					break;
				case TCARole.BALANCE:
					balanced_engines.Add(e);
					break;
				case TCARole.MANUAL:
					manual_engines.Add(e);
					break;
				}
			}
			//balance-only engines
			calculate_torque(manual_engines);
			if(balanced_engines.Count > 0)
				optimize_torque_iteratively(balanced_engines, Vector3.zero);
			calculate_torque(manual_engines, balanced_engines);
			normilize_limits = torque.IsSmallerThan(TCAConfiguration.Globals.OptimizationTorqueCutoff) 
				&& steering_engines.Count > maneuver_engines.Count;
			//optimize limits for steering
			update_torque_limits(steering_engines);
			if(steering_engines.Count > 0)
				steer_with_engines(steering_engines);
			//set thrust limiters of engines
			for(int i = 0; i < num_active; i++)
			{
				var e = active_engines[i];
				if(e.Role == TCARole.MANUAL) continue;
				e.thrustPercentage = Mathf.Clamp(100 * e.VSF * e.limit, 0f, 100f);
			}
		}

		void initialize_engines(IList<EngineWrapper> engines)
		{
			normilize_limits = true;
			var num_engines = engines.Count;
			//calculate specific torques and min imbalance
			var min_imbalance = Vector3.zero;
			for(int i = 0; i < num_engines; i++)
			{
				var e = engines[i];
				e.InitState();
				e.thrustDirection = refT.InverseTransformDirection(e.thrustInfo.dir);
				var lever = e.thrustInfo.pos-wCoM;
				e.specificTorque = refT.InverseTransformDirection(Vector3.Cross(lever, e.thrustInfo.dir));
				e.torqueRatio = Mathf.Pow(Mathf.Clamp01(1-Mathf.Abs(Vector3.Dot(lever.normalized, e.thrustInfo.dir))), TCAConfiguration.Globals.TorqueRatioFactor);
				min_imbalance += e.Torque(0);
			}
			//calculate engine's torue, torque limits and set VSF
			if(IsStateSet(TCAState.VerticalSpeedControl))
			{
				//correct VerticalSpeedFactor if needed
				if(!min_imbalance.IsZero())
				{
					var anti_min_imbalance = Vector3.zero;
					for(int i = 0; i < num_engines; i++)
					{
						var e = engines[i];
						if(Vector3.Dot(e.specificTorque, min_imbalance) < 0)
							anti_min_imbalance += e.specificTorque * e.nominalCurrentThrust(1);
					}
					anti_min_imbalance = Vector3.Project(anti_min_imbalance, min_imbalance);
					VerticalSpeedFactor = Mathf.Clamp(VerticalSpeedFactor, 
					                              Mathf.Clamp01(min_imbalance.magnitude/anti_min_imbalance.magnitude
					                                            *TCAConfiguration.Globals.VSF_BalanceCorrection), 1f);
				}
				for(int i = 0; i < num_engines; i++)
				{
					var e = engines[i];
					if(e.isVSC)
					{
						if(e.VSF > 0) e.VSF = VerticalSpeedFactor;
						e.throttle = e.VSF * vessel.ctrlState.mainThrottle;
					}
					else 
					{
						e.throttle = vessel.ctrlState.mainThrottle;
						e.VSF = 1f;
					}
					e.currentTorque = e.Torque(e.throttle);
					e.currentTorque_m = e.currentTorque.magnitude;
				}
			}
			else
			{
				for(int i = 0; i < num_engines; i++)
				{
					var e = engines[i];
					e.VSF = 1f;
					e.throttle = vessel.ctrlState.mainThrottle;
					e.currentTorque = e.Torque(e.throttle);
					e.currentTorque_m = e.currentTorque.magnitude;
				}
			}
		}

		void update_torque_limits(IList<EngineWrapper> engines)
		{
			e_torque_limits = new Vector6();
			for(int i = 0; i < engines.Count; i++)
				e_torque_limits.Add(engines[i].currentTorque);
		}

		void calculate_torque(params IList<EngineWrapper>[] engines)
		{
			torque = Vector3.zero;
			for(int i = 0; i < engines.Length; i++)
			{
				for(int j = 0; j < engines[i].Count; j++)
				{
					var e = engines[i][j];
					torque += e.Torque(e.throttle * e.limit);
				}
			}
		}

		void update_rwheels()
		{
			r_torque_limits = new Vector6();
			for(int i = 0; i < RWheels.Count; i++)
			{
				var w = RWheels[i];
				if(!w.operational) continue;
				r_torque_limits.Add(refT.InverseTransformDirection(new Vector3(w.PitchTorque, w.RollTorque, w.YawTorque)));
			}
		}

		static bool optimize_torque(IList<EngineWrapper> engines, int num_engines, Vector3 target, float target_m, float eps)
		{
			var compensation = Vector3.zero;
			var maneuver = Vector3.zero;
			for(int i = 0; i < num_engines; i++)
			{
				var e = engines[i];
				e.limit_tmp = -Vector3.Dot(e.currentTorque, target)/target_m/e.currentTorque_m*e.torqueRatio;
				if(e.limit_tmp > 0)
					compensation += e.specificTorque * e.nominalCurrentThrust(e.throttle * e.limit);
				else if(e.Role == TCARole.MANEUVER)
				{
					if(e.limit.Equals(0)) e.limit = eps;
					maneuver +=  e.specificTorque * e.nominalCurrentThrust(e.throttle * e.limit);
				} else e.limit_tmp = 0f;
			}
			var compensation_m = compensation.magnitude;
			var maneuver_m = maneuver.magnitude;
			if(compensation_m < eps && maneuver_m.Equals(0)) return false;
			var limits_norm = Mathf.Clamp01(target_m/compensation_m);
			var maneuver_norm = Mathf.Clamp01(target_m/maneuver_m);
			for(int i = 0; i < num_engines; i++)
			{
				var e = engines[i];
				e.limit = e.limit_tmp > 0 ? 
					Mathf.Clamp01(e.limit * (1 - e.limit_tmp * limits_norm)) : 
					Mathf.Clamp01(e.limit * (1 - e.limit_tmp * maneuver_norm));
			}
			return true;
		}

		bool optimize_torque_iteratively(List<EngineWrapper> engines, Vector3 needed_torque)
		{
			var num_engines = engines.Count;
			var zero_torque = needed_torque.IsZero();
			TorqueAngle = -1f;
			TorqueError = -1f;
			float error, angle;
			var last_error = -1f;
			Vector3 cur_imbalance, target;
			for(int i = 0; i < TCAConfiguration.Globals.MaxIterations; i++)
			{
				//calculate current errors and target
				cur_imbalance = torque;
				for(int j = 0; j < num_engines; j++) 
				{ var e = engines[j]; cur_imbalance += e.Torque(e.throttle * e.limit); }
				angle  = zero_torque? 0f : Vector3.Angle(cur_imbalance, needed_torque);
				target = needed_torque-cur_imbalance;
				error  = target.magnitude;
				//remember the best state
				if(angle <= 0f && error < TorqueError || angle+error < TorqueAngle+TorqueError || TorqueAngle < 0) 
				{ 
					for(int j = 0; j < num_engines; j++) 
					{ var e = engines[j]; e.best_limit = e.limit; }
					TorqueAngle = angle;
					TorqueError = error;
				}
				//check convergence conditions
				if(error < TCAConfiguration.Globals.OptimizationPrecision || 
				   last_error > 0 && Mathf.Abs(error-last_error) < TCAConfiguration.Globals.OptimizationPrecision/10)
					break;
				last_error = error;
				//normalize limits before optimization
				if(normilize_limits)
				{   //this is much faster than linq
					var limit_norm = 0f;
					for(int j = 0; j < num_engines; j++) 
					{ 
						var e = engines[j];
						if(limit_norm < e.limit) limit_norm = e.limit; 
					}
					if(limit_norm > 0)
					{
						for(int j = 0; j < num_engines; j++) 
						{ var e = engines[j]; e.limit = Mathf.Clamp01(e.limit / limit_norm); }
					}
				}
				//optimize limits
				if(!optimize_torque(engines, num_engines, target, error, TCAConfiguration.Globals.OptimizationPrecision)) 
					break;
			}
			var optimized = TorqueError < TCAConfiguration.Globals.OptimizationTorqueCutoff || 
				(!zero_torque && TorqueAngle < TCAConfiguration.Globals.OptimizationAngleCutoff);
			//treat single-engine crafts specially
			if(num_engines == 1) 
				engines[0].limit = optimized? 1f : 0f;
			else //restore the best state
				for(int j = 0; j < num_engines; j++) 
				{ var e = engines[j]; e.limit = e.best_limit; }
			return optimized;
		}

		void steer_with_engines(List<EngineWrapper> engines)
		{
			//calculate steering
			if(CFG.AutoTune) tune_steering_params();
			steering = new Vector3(vessel.ctrlState.pitch, vessel.ctrlState.roll, vessel.ctrlState.yaw);
			if(!steering.IsZero())
			{
				steering = steering/steering.CubeNorm().magnitude;
				if(!CFG.AutoTune) steering *= CFG.SteeringGain;
				steering.Scale(CFG.SteeringModifier);
			}
			//calculate needed torque
			var needed_torque = Vector3.zero;
			for(int i = 0; i < engines.Count; i++)
			{
				var e = engines[i];
				if(Vector3.Dot(e.currentTorque, steering) > 0)
					needed_torque += e.currentTorque;
			}
			needed_torque = e_torque_limits.Clamp(Vector3.Project(needed_torque, steering) * steering.magnitude);
			//optimize engines; if failed, set the flag and kill torque if requested
			if(!optimize_torque_iteratively(engines, needed_torque) && 
			   !needed_torque.IsZero())
			{
//				DebugEngines(engines, needed_torque);//debug
				for(int j = 0; j < engines.Count; j++) engines[j].InitLimits();
				optimize_torque_iteratively(engines, Vector3.zero);
				State |= TCAState.Unoptimized;
			}
//			DebugEngines(engines, needed_torque);//debug
		}

		#if DEBUG
		void DebugEngines(IList<EngineWrapper> engines, Vector3 needed_torque)
		{
			Utils.Log("Engines:\n"+
			          engines.Aggregate("", (s, e) => s 
			                            +string.Format("engine(vec{0}, vec{1}, vec{2}, {3}, {4}),\n",
			                                           refT.InverseTransformDirection(e.thrustInfo.pos-wCoM),
			                                           e.thrustDirection,e.specificTorque, e.minThrust, e.maxThrust)));
			Utils.Log("Engines Torque:\n"+engines.Aggregate("", (s, e) => s + "vec"+e.Torque(e.throttle*e.limit)+",\n"));
			Utils.Log(
			          "Steering: {0}\n" +
			          "Needed Torque: {1}\n" +
			          "Torque Imbalance: {2}\n" +
			          "Torque Error: {3}kNm, {4}deg\n" +
			          "Torque Clamp:\n   +{5}\n   -{6}\n" +
			          "Limits: [{7}]", 
			          steering,
			          needed_torque,
			          engines.Aggregate(Vector3.zero, (v,e) => v+e.Torque(e.throttle*e.limit)),
			          TorqueError, TorqueAngle,
			          e_torque_limits.positive, 
			          e_torque_limits.negative,
			          engines.Aggregate("", (s, e) => s+e.limit+" ").Trim()
			         );
		}
		#endif

		void update_vessel_stats(IList<EngineWrapper> engines)
		{
			accel_speed = 0f; decel_speed = 0f; maxTWR = 0f;
			if(!CFG.ControlAltitude && CFG.VerticalCutoff >= TCAConfiguration.Globals.MaxVS || !OnPlanet) return;
			//calculate vertical speed and acceleration
			//unlike the vessel.verticalSpeed, this method is unaffected by ship's rotation 
			var upV = (float)Vector3d.Dot(vessel.srf_velocity, up); //from MechJeb
			VerticalAccel = 0.3f*VerticalAccel + 0.7f*(upV-VerticalSpeed)/TimeWarp.fixedDeltaTime; //high-freq filter
			VerticalSpeed = upV;
			//calculate total downward thrust and slow engines' corrections
			var down_thrust = 0f;
			var slow_thrust = 0f;
			var fast_thrust = 0f;
			for(int i = 0; i < engines.Count; i++)
			{
				var e = engines[i];
				e.VSF = 1f;
				if(e.thrustInfo == null) continue;
				if(e.isVSC)
				{
					var dcomponent = -Vector3.Dot(e.thrustInfo.dir, up);
					if(dcomponent <= 0) e.VSF = 0f;
					else 
					{
						var dthrust = e.nominalCurrentThrust(e.best_limit)*e.thrustMod*dcomponent;
						if(e.useEngineResponseTime && dthrust > 0) 
						{
							slow_thrust += dthrust;
							accel_speed += e.engineAccelerationSpeed*dthrust;
							decel_speed += e.engineDecelerationSpeed*dthrust;
						}
						else fast_thrust = dthrust;
						down_thrust += dthrust;
					}
				} 
			}
			maxTWR = down_thrust/9.81f/vessel.GetTotalMass();
			var controllable_thrust = slow_thrust+fast_thrust;
			if(controllable_thrust.Equals(0)) return;
			//correct setpoint for current TWR and slow engines
			if(accel_speed > 0) accel_speed = controllable_thrust/accel_speed*TCAConfiguration.Globals.ASf;
			if(decel_speed > 0) decel_speed = controllable_thrust/decel_speed*TCAConfiguration.Globals.DSf;
		}

		void update_VerticalCutoff()
		{
			if(!CFG.ControlAltitude || !OnPlanet) return;
			State |= TCAState.AltitudeControl;
			Altitude = current_altitude;
			var alt_error = CFG.DesiredAltitude-Altitude;
			if((accel_speed > 0 || decel_speed > 0))
			{
				if(VerticalSpeed > 0)
				jets_alt_controller.P = Mathf.Clamp(TCAConfiguration.Globals.AltErrF*Mathf.Abs(alt_error/VerticalSpeed), 
					                               0, jets_alt_controller.D);
				else if(VerticalSpeed < 0)
					jets_alt_controller.P = Mathf.Clamp(Mathf.Pow(maxTWR, TCAConfiguration.Globals.AltTWRp)/Mathf.Abs(VerticalSpeed), 
					                               0, Utils.ClampH(jets_alt_controller.D/maxTWR/TCAConfiguration.Globals.AltTWRd, jets_alt_controller.D));
				else jets_alt_controller.P = TCAConfiguration.Globals.JetsAltitudeController.P;
				jets_alt_controller.Update(alt_error);
				CFG.VerticalCutoff = jets_alt_controller.Action;
			}
			else 
			{
				alt_controller.Update(alt_error);
				CFG.VerticalCutoff = alt_controller.Action;
			}
//			Utils.CSV(Altitude, CFG.VerticalCutoff, VerticalSpeedFactor, VerticalSpeed);//debug
		}

		void update_VerticalSpeedFactor(IList<EngineWrapper> engines)
		{
			VerticalSpeedFactor = 1f;
			if(CFG.VerticalCutoff >= TCAConfiguration.Globals.MaxVS || !OnPlanet) return;
			State |= TCAState.VerticalSpeedControl;
			var upAF = -VerticalAccel
				*(VerticalAccel < 0? accel_speed : decel_speed)*TCAConfiguration.Globals.UpAf;
			var setpoint = CFG.VerticalCutoff;
			if(!maxTWR.Equals(0))
				setpoint = CFG.VerticalCutoff+(TCAConfiguration.Globals.VSF_TWRf+upAF)/maxTWR;
			//calculate new VSF
			var err = setpoint-VerticalSpeed;
			var K = Mathf.Clamp01(err
			                      /TCAConfiguration.Globals.K0
			                      /Mathf.Pow(Utils.ClampL(VerticalAccel/TCAConfiguration.Globals.K1+1, TCAConfiguration.Globals.L1), 2f)
			                      +upAF);
			VerticalSpeedFactor = vessel.LandedOrSplashed? K : Utils.ClampL(K, TCAConfiguration.Globals.MinVSF);
			//loosing altitude alert
			if(VerticalSpeed < 0 && VerticalSpeed < CFG.VerticalCutoff-0.1f && !vessel.LandedOrSplashed)
				State |= TCAState.LoosingAltitude;
//			Utils.CSV(VerticalSpeed, CFG.VerticalCutoff, maxTWR, VerticalAccel, upAF, setpoint-CFG.VerticalCutoff, K);//debug
		}

		void block_throttle(FlightCtrlState s)
		{ if(Available && CFG.Enabled && CFG.BlockThrottle) s.mainThrottle = 1f; }

		void kill_horizontal_velocity(FlightCtrlState s)
		{
			if(!Available || !CFG.Enabled || !CFG.KillHorVel || refT == null || !OnPlanet) return;
			//allow user to intervene
			if(!Mathfx.Approx(s.pitch, s.pitchTrim, 0.1f) ||
			   !Mathfx.Approx(s.roll, s.rollTrim, 0.1f) ||
			   !Mathfx.Approx(s.yaw, s.yawTrim, 0.1f)) return;
			//if the vessel is not moving, nothing to do
			if(vessel.LandedOrSplashed || vessel.srfSpeed < 0.01) return;
			//calculate total current thrust
			var thrust = Engines.Aggregate(Vector3.zero, (v, e) => v + e.thrustDirection*e.finalThrust);
			if(thrust.IsZero()) return;
			//disable SAS
			vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
			//calculate horizontal velocity
			var hV  = Vector3d.Exclude(up, vessel.srf_velocity);
			var hVm = hV.magnitude;
			//calculate needed thrust direction
			var MaxHv = Math.Max(vessel.acceleration.magnitude*TCAConfiguration.Globals.AccelerationFactor, TCAConfiguration.Globals.MinHvThreshold);
			var max_torque = e_torque_limits.Max+r_torque_limits.Max;
			Vector3 needed_thrust_dir;
			if(hVm > 1e-7)
			{
				//correction for low TWR and torque
				var upl  = refT.InverseTransformDirection(up);
				var hVl  = refT.InverseTransformDirection(hV);
				var TWR  = Vector3.Dot(thrust, upl) < 0? Vector3.Project(thrust, upl).magnitude/9.81f/vessel.GetTotalMass() : 0f;
				var twrF = Utils.ClampH(TWR/TCAConfiguration.Globals.TWRf, 1);
				var torF = Utils.ClampH(Vector3.Scale(Vector3.ProjectOnPlane(max_torque, hVl), MoI.Inverse()).sqrMagnitude, 1);
				var upF  = Vector3.Dot(thrust, hVl) < 0? 1 : Mathf.Pow(Utils.ClampL(twrF*torF, 1e-9f), TCAConfiguration.Globals.upF);
				needed_thrust_dir = hVl.normalized - upl*Utils.ClampL((float)(MaxHv/hVm), 1)/upF;
//				Utils.Log("needed thrust direction: {0}\n" +
//				          "TWR factor: {1}\n" +
//				          "torque factor: {2}\n" +
//				          "up factor: {3}\n" +
//				          "TWR: {4}\n" +
//				          "torque limits {5}\n" +
//				          "MoI {6}\n", 
//				          needed_thrust_dir,
//				          twrF,
//				          torF,
//				          upF, 
//				          TWR, 
//				          max_torque, 
//				          MoI
//				         );//debug
			}
			else needed_thrust_dir = refT.InverseTransformDirection(-up);
			//calculate corresponding rotation
			var attitude_error = Quaternion.FromToRotation(needed_thrust_dir, thrust);
			var steering_error = new Vector3(Utils.CenterAngle(attitude_error.eulerAngles.x),
			                                 Utils.CenterAngle(attitude_error.eulerAngles.y),
			                                 Utils.CenterAngle(attitude_error.eulerAngles.z))/180*Mathf.PI;
			//tune PID parameters and steering_error
			var angularM = Vector3.Scale(vessel.angularVelocity, MoI);
			var inertia  = Vector3.Scale(angularM.Sign(),
			                             Vector3.Scale(Vector3.Scale(angularM, angularM),
			                                           Vector3.Scale(max_torque, MoI).Inverse()))
				.ClampComponents(-Mathf.PI, Mathf.PI);
			var Tf = Mathf.Clamp(1/angularA.magnitude, TCAConfiguration.Globals.MinTf, TCAConfiguration.Globals.MaxTf);
			steering_error += inertia / Mathf.Lerp(TCAConfiguration.Globals.InertiaFactor, 1, 
			                                       MoI.magnitude*TCAConfiguration.Globals.MoIFactor);
			Vector3.Scale(steering_error, angularA.normalized);
			hV_controller.D = Mathf.Lerp(TCAConfiguration.Globals.MinHvD, 
			                             TCAConfiguration.Globals.MaxHvD, 
			                             angularM.magnitude*TCAConfiguration.Globals.AngularMomentumFactor);
			hV_controller.I = hV_controller.P / (TCAConfiguration.Globals.HvI_Factor * Tf/TCAConfiguration.Globals.MinTf);
			//update PID controller and set steering
			hV_controller.Update(steering_error, vessel.angularVelocity);
			s.pitch = hV_controller.Action.x;
			s.roll  = hV_controller.Action.y;
			s.yaw   = hV_controller.Action.z;
			#if DEBUG
//			Utils.Log(//debug
//			          "hV: {0}\n" +
//			          "Thrust: {1}\n" +
//			          "Needed Thrust Dir: {2}\n" +
//			          "H-T Angle: {3}\n" +
//			          "Steering error: {4}\n" +
//			          "Down comp: {5}\n" +
//			          "omega: {6}\n" +
//			          "Tf: {7}\n" +
//			          "PID: {8}\n" +
//			          "angularA: {9}\n" +
//			          "inertia: {10}\n" +
//			          "angularM: {11}\n" +
//			          "inertiaF: {12}\n" +
//			          "MaxHv: {13}\n" +
//			          "MoI: {14}",
//			          refT.InverseTransformDirection(hV),
//			          thrust, needed_thrust_dir,
//			          Vector3.Angle(refT.InverseTransformDirection(hV), needed_thrust_dir),
//			          steering_error,
//			          Utils.ClampL(10/(float)hVm, 1),
//			          vessel.angularVelocity, Tf,
//			          new Vector3(hV_controller.P, hV_controller.I, hV_controller.D),
//			          angularA, inertia, angularM,
//			          Mathf.Lerp(TCAConfiguration.Globals.InertiaFactor, 1, 
//			                     MoI.magnitude*TCAConfiguration.Globals.MoIFactor),
//			          MaxHv,
//			          MoI
//			);
			#endif
		}

		void tune_steering_params()
		{
			//calculate maximum angular acceleration for each axis
			var max_torque = e_torque_limits.Max;
			var new_angularA = new Vector3
				(
					!MoI.x.Equals(0)? max_torque.x/MoI.x : float.MaxValue,
					!MoI.y.Equals(0)? max_torque.y/MoI.y : float.MaxValue,
					!MoI.z.Equals(0)? max_torque.z/MoI.z : float.MaxValue
				);
			angularA_filter.Update(new_angularA - angularA);
			angularA += angularA_filter.Action;
			//tune steering modifiers
			CFG.SteeringModifier.x = Mathf.Clamp(TCAConfiguration.Globals.SteeringCurve.Evaluate(angularA.x)/100f, 0, 1);
			CFG.SteeringModifier.y = Mathf.Clamp(TCAConfiguration.Globals.SteeringCurve.Evaluate(angularA.y)/100f, 0, 1);
			CFG.SteeringModifier.z = Mathf.Clamp(TCAConfiguration.Globals.SteeringCurve.Evaluate(angularA.z)/100f, 0, 1);
			//tune PI coefficients
			CFG.Engines.P = TCAConfiguration.Globals.EnginesCurve.Evaluate(angularA.magnitude);
			CFG.Engines.I = CFG.Engines.P/2f;
			#if DEBUG
//			Utils.Log("max_torque: {0}\n" + //debug
//                    "MoI {1}\n" +
//                    "angularA {2}", 
//                     max_torque, MoI, angularA);
			#endif
		}
		#endregion

		#region From MechJeb2
		// KSP's calculation of the vessel's moment of inertia is broken.
		// This function is somewhat expensive :(
		// Maybe it can be optimized more.
		static readonly Vector3[] unitVectors = { new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1) };
		void updateMoI()
		{
			if(vessel == null || vessel.rigidbody == null) return;
			inertiaTensor = new Matrix3x3f();
			Transform vesselTransform = vessel.GetTransform();
			Quaternion inverseVesselRotation = Quaternion.Inverse(vesselTransform.rotation);
			foreach(Part p in vessel.parts)
			{
				var rb = p.Rigidbody;
				if (rb == null) continue;
				//Compute the contributions to the vessel inertia tensor due to the part inertia tensor
				Vector3 principalMoments = rb.inertiaTensor;
				Quaternion principalAxesRot = inverseVesselRotation * p.transform.rotation * rb.inertiaTensorRotation;
				Quaternion invPrincipalAxesRot = Quaternion.Inverse(principalAxesRot);
				for (int j = 0; j < 3; j++)
				{
					Vector3 partInertiaTensorTimesjHat = principalAxesRot * Vector3.Scale(principalMoments, invPrincipalAxesRot * unitVectors[j]);
					for (int i = 0; i < 3; i++)
						inertiaTensor[i, j] += Vector3.Dot(unitVectors[i], partInertiaTensorTimesjHat);
				}
				//Compute the contributions to the vessel inertia tensor due to the part mass and position
				float partMass = p.TotalMass();
				Vector3 partPosition = vesselTransform.InverseTransformDirection(rb.worldCenterOfMass - wCoM);
				for(int i = 0; i < 3; i++)
				{
					inertiaTensor[i, i] += partMass * partPosition.sqrMagnitude;
					for (int j = 0; j < 3; j++)
						inertiaTensor[i, j] += -partMass * partPosition[i] * partPosition[j];
				}
			}
			MoI = new Vector3(inertiaTensor[0, 0], inertiaTensor[1, 1], inertiaTensor[2, 2]);
			MoI = refT.InverseTransformDirection(vessel.transform.TransformDirection(MoI));
		}
		#endregion
	}

	/// <summary>
	/// Binary flags of TCA state.
	/// They should to be checked in this particular order, as they are set sequentially:
	/// If a previous flag is not set, the next ones are not either.
	/// </summary>
	[Flags] public enum TCAState 
	{ 
		Disabled 			   = 0,
		Enabled 			   = 1 << 0,
		Throttled 			   = 1 << 1,
		HaveEC 				   = 1 << 2, 
		HaveActiveEngines 	   = 1 << 3,
		VerticalSpeedControl   = 1 << 4,
		AltitudeControl        = 1 << 5,
		LoosingAltitude 	   = 1 << 6,
		Unoptimized			   = 1 << 7,
		Nominal				   = Enabled | Throttled | HaveEC | HaveActiveEngines,
		NoActiveEngines        = Enabled | Throttled | HaveEC,
		NoEC                   = Enabled | Throttled,
	}
}
