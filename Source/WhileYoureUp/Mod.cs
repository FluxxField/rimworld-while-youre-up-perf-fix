using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Xml;
using CodeOptimist;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace WhileYoureUp;

internal class Mod : Verse.Mod
{
	public enum DetourType
	{
		Inactive,
		HtcOpportunity,
		HtcBeforeCarry,
		ExistingElsePuah,
		Puah,
		PuahOpportunity,
		PuahBeforeCarry
	}

	public record BaseDetour
	{
		public record struct Puah(Dictionary<ThingDef, IntVec3> defHauls);

		public record struct Opportunity(LocalTargetInfo jobTarget, List<(Thing thing, IntVec3 storeCell)> hauls)
		{
			public record struct OpportunityPuah(IntVec3 startCell, int unloadedTick)
			{
				public static List<(Thing thing, IntVec3 storeCell)> haulsByUnloadDistanceOrdered;

				public static List<(Thing thing, IntVec3 storeCell)> haulsByUnloadDistancePending;
			}

			public OpportunityPuah puah = default(OpportunityPuah);
		}

		public record struct BeforeCarry(LocalTargetInfo carryTarget)
		{
			public record struct BeforeCarryPuah(IntVec3 storeCell);

			public BeforeCarryPuah puah = default(BeforeCarryPuah);
		}

		public static Pawn lastPawn;

		public static int lastFrameCount;

		public static string lastCallerName;

		public DetourType type;

		public Puah puah = new Puah
		{
			defHauls = new Dictionary<ThingDef, IntVec3>(16)
		};

		public Opportunity opportunity = new Opportunity
		{
			hauls = new List<(Thing, IntVec3)>(16)
		};

		public BeforeCarry beforeCarry;

		public static Job CatchLoop_Job(Pawn pawn, Job job, [CallerMemberName] string callerName = "")
		{
			lastPawn = pawn;
			lastFrameCount = RealTime.frameCount;
			lastCallerName = callerName;
			return job;
		}

		public void Deactivate()
		{
			type = DetourType.Inactive;
			puah.defHauls.Clear();
			opportunity.hauls.Clear();
		}

		public void TrackPuahThing(Thing thing, IntVec3 storeCell, bool prepend = false, bool trackDef = true)
		{
			if (trackDef)
			{
				puah.defHauls.SetOrAdd(thing.def, storeCell);
			}
			if (type != DetourType.PuahOpportunity)
			{
				return;
			}
			if (opportunity.hauls.LastOrDefault().thing == thing)
			{
				opportunity.hauls.Pop();
			}
			if (prepend)
			{
				if (opportunity.hauls.FirstOrDefault().thing == thing)
				{
					opportunity.hauls.RemoveAt(0);
				}
				opportunity.hauls.Insert(0, (thing, storeCell));
			}
			else
			{
				opportunity.hauls.Add((thing, storeCell));
			}
		}

		public void GetJobReport(ref string text, bool isLoad)
		{
			if (type != DetourType.Inactive)
			{
				text = text.TrimEnd(new char[1] { '.' });
				string text2 = (isLoad ? "_LoadReport" : "_UnloadReport");
				string text3;
				switch (type)
				{
					case DetourType.Puah:
						text3 = ("PickUpAndHaulPlus" + text2).ModTranslate(text.Named("ORIGINAL"));
						break;
					case DetourType.HtcOpportunity:
					case DetourType.PuahOpportunity:
						text3 = ("Opportunity" + text2).ModTranslate(text.Named("ORIGINAL"), opportunity.jobTarget.Label.Named("DESTINATION"));
						break;
					case DetourType.HtcBeforeCarry:
					case DetourType.PuahBeforeCarry:
						text3 = ("HaulBeforeCarry" + text2).ModTranslate(text.Named("ORIGINAL"), beforeCarry.carryTarget.Label.Named("DESTINATION"));
						break;
					default:
						text3 = text;
						break;
				}
				text = text3;
			}
		}

		public bool TrackPuahThingIfOpportune(Thing thing, Pawn pawn, ref IntVec3 foundCell)
		{
			bool flag = pawn.carryTracker?.CarriedThing == thing;
			TrackPuahThing(thing, foundCell, flag, trackDef: false);
			float num = 0f;
			IntVec3 a = opportunity.puah.startCell;
			foreach (var haul in opportunity.hauls)
			{
				Thing item = haul.thing;
				num += a.DistanceTo(item.Position);
				a = item.Position;
			}
			List<(Thing thing, IntVec3 storeCell)> haulsByUnloadDistance = Opportunity.OpportunityPuah.haulsByUnloadDistanceOrdered ?? (Opportunity.OpportunityPuah.haulsByUnloadDistanceOrdered = new List<(Thing, IntVec3)>(16));
			List<(Thing, IntVec3)> list = Opportunity.OpportunityPuah.haulsByUnloadDistancePending ?? (Opportunity.OpportunityPuah.haulsByUnloadDistancePending = new List<(Thing, IntVec3)>(16));
			haulsByUnloadDistance.Clear();
			haulsByUnloadDistance.Add(opportunity.hauls.First());
			list.Clear();
			list.AddRange(opportunity.hauls.GetRange(1, opportunity.hauls.Count - 1));
			while (list.Count > 0)
			{
				(Thing, IntVec3) item2 = list.MinBy<(Thing, IntVec3), int>(((Thing thing, IntVec3 storeCell) x) => x.storeCell.DistanceToSquared(haulsByUnloadDistance.Last().storeCell));
				haulsByUnloadDistance.Add(item2);
				list.Remove(item2);
			}
			float num2 = opportunity.hauls.Last().thing.Position.DistanceTo(haulsByUnloadDistance.First().storeCell);
			float num3 = 0f;
			IntVec3 a2 = haulsByUnloadDistance.First().storeCell;
			foreach (var item4 in haulsByUnloadDistance)
			{
				IntVec3 item3 = item4.storeCell;
				num3 += a2.DistanceTo(item3);
				a2 = item3;
			}
			float num4 = haulsByUnloadDistance.Last().storeCell.DistanceTo(opportunity.jobTarget.Cell);
			float num5 = opportunity.puah.startCell.DistanceTo(opportunity.jobTarget.Cell);
			float num6 = num + num2 + num3 + num4;
			float num7 = num5 * settings.Opportunity_MaxTotalTripPctOrigTrip;
			float num8 = num + num3 + num4;
			float num9 = num5 * settings.Opportunity_MaxNewLegsPctOrigTrip;
			if (num6 > num7 || num8 > num9)
			{
				foundCell = IntVec3.Invalid;
				opportunity.hauls.RemoveAt((!flag) ? (opportunity.hauls.Count - 1) : 0);
				return false;
			}
			puah.defHauls.SetOrAdd(thing.def, foundCell);
			return true;
		}

		[CompilerGenerated]
		protected BaseDetour(BaseDetour original)
		{
			type = original.type;
			puah = original.puah;
			opportunity = original.opportunity;
			beforeCarry = original.beforeCarry;
		}

		public BaseDetour()
		{
		}
	}

	[HarmonyPatch]
	private static class Puah_WorkGiver_HaulToInventory__JobOnThing_Patch
	{
		[HarmonyPostfix]
		private static void TrackInitialHaul(Job __result, Pawn pawn, Thing thing)
		{
			if (__result != null && settings.Enabled && settings.UsePickUpAndHaulPlus)
			{
				SetOrAddDetour(pawn, DetourType.ExistingElsePuah).TrackPuahThing(thing, __result.targetB.Cell, prepend: true);
			}
		}

		private static bool Prepare()
		{
			return havePuah;
		}

		private static MethodBase TargetMethod()
		{
			return PuahMethod_WorkGiver_HaulToInventory_JobOnThing;
		}

		[HarmonyPriority(500)]
		private static void Prefix(MethodBase __originalMethod)
		{
			PushHtiMethod(__originalMethod);
		}

		[HarmonyPriority(300)]
		private static void Postfix()
		{
			PopHtiMethod();
		}

		[HarmonyPrefix]
		private static void HaulToEqualPriority(Pawn pawn, Thing thing)
		{
			if (!settings.Enabled || !settings.UsePickUpAndHaulPlus || !settings.HaulBeforeCarry_ToEqualPriority)
			{
				return;
			}
			BaseDetour valueSafe = detours.GetValueSafe(pawn);
			if ((object)valueSafe == null || valueSafe.type != DetourType.PuahBeforeCarry)
			{
				return;
			}
			IHaulDestination haulDestination = StoreUtility.CurrentHaulDestinationOf(thing);
			if (haulDestination == null)
			{
				return;
			}
			reducedPriorityStore = haulDestination.GetStoreSettings();
			if (thingsInReducedPriorityStore == null)
			{
				thingsInReducedPriorityStore = new List<Thing>(32);
			}
			thingsInReducedPriorityStore.AddRange(thing.GetSlotGroup().CellsList.SelectMany((IntVec3 cell) => from cellThing in cell.GetThingList(thing.Map)
																											  where cellThing.def.EverHaulable
																											  select cellThing));
			thing.Map.haulDestinationManager.Notify_HaulDestinationChangedPriority();
		}

		[HarmonyPostfix]
		private static void HaulToEqualPriorityCleanup()
		{
			if (reducedPriorityStore != null)
			{
				var owner = Traverse.Create(reducedPriorityStore).Property<IHaulDestination>("HaulDestinationOwner").Value;
				var obj = owner?.Map;
				reducedPriorityStore = null;
				thingsInReducedPriorityStore.Clear();
				obj?.haulDestinationManager.Notify_HaulDestinationChangedPriority();
			}
		}
	}

	[HarmonyPatch(typeof(JobDriver_HaulToCell), "GetReport")]
	private static class JobDriver_HaulToCell__GetReport_Patch
	{
		[HarmonyPostfix]
		private static void GetDetourReport(JobDriver_HaulToCell __instance, ref string __result)
		{
			detours.GetValueSafe(__instance.pawn)?.GetJobReport(ref __result, isLoad: true);
		}
	}

	[HarmonyPatch]
	private static class Puah_JobDriver__GetReport_Patch
	{
		private static bool Prepare()
		{
			return havePuah;
		}

		private static MethodBase TargetMethod()
		{
			return AccessTools.DeclaredMethod(typeof(JobDriver), "GetReport");
		}

		[HarmonyPostfix]
		private static void GetDetourReport(JobDriver __instance, ref string __result)
		{
			if (settings.Enabled && settings.UsePickUpAndHaulPlus)
			{
				bool flag = PuahType_JobDriver_HaulToInventory.IsInstanceOfType(__instance);
				bool flag2 = PuahType_JobDriver_UnloadYourHauledInventory.IsInstanceOfType(__instance);
				if (flag || flag2)
				{
					detours.GetValueSafe(__instance.pawn)?.GetJobReport(ref __result, flag);
				}
			}
		}
	}

	[HarmonyPatch(typeof(JobDriver_HaulToCell), "MakeNewToils")]
	private static class JobDriver_HaulToCell__MakeNewToils_Patch
	{
		[HarmonyPostfix]
		private static void ClearDetourOnFinish(JobDriver __instance)
		{
			__instance.AddFinishAction(delegate
			{
				BaseDetour valueSafe = detours.GetValueSafe(__instance.pawn);
				DetourType? detourType = valueSafe?.type;
				bool flag;
				if (detourType.HasValue)
				{
					DetourType valueOrDefault = detourType.GetValueOrDefault();
					if ((uint)(valueOrDefault - 1) <= 1u)
					{
						flag = true;
						goto IL_004e;
					}
				}
				flag = false;
				goto IL_004e;
			IL_004e:
				if (flag)
				{
					valueSafe.Deactivate();
				}
			});
		}
	}

	[HarmonyPatch]
	private static class Puah_JobDriver_UnloadYourHauledInventory__MakeNewToils_Patch
	{
		private static bool Prepare()
		{
			return havePuah;
		}

		private static MethodBase TargetMethod()
		{
			return PuahMethod_JobDriver_UnloadYourHauledInventory_MakeNewToils;
		}

		[HarmonyPostfix]
		private static void ClearDetourOnFinish(JobDriver __instance)
		{
			__instance.AddFinishAction(delegate
			{
				BaseDetour valueSafe = detours.GetValueSafe(__instance.pawn);
				if ((object)valueSafe != null)
				{
					if (valueSafe.type == DetourType.PuahOpportunity)
					{
						valueSafe.opportunity.puah.unloadedTick = RealTime.frameCount;
					}
					valueSafe.Deactivate();
				}
			});
		}
	}

	[HarmonyPatch(typeof(Pawn_JobTracker), "ClearQueuedJobs")]
	private static class Pawn_JobTracker__ClearQueuedJobs_Patch
	{
		[HarmonyPostfix]
		private static void ClearDetour(Pawn ___pawn)
		{
			if (___pawn != null)
			{
				detours.GetValueSafe(___pawn)?.Deactivate();
			}
		}
	}

	[HarmonyPatch(typeof(Pawn), "Destroy")]
	private static class Pawn__Destroy_Patch
	{
		[HarmonyPostfix]
		private static void ClearDetour(Pawn __instance)
		{
			detours.Remove(__instance);
		}
	}

	[HarmonyPatch(typeof(WorkGiver_Scanner), "HasJobOnThing")]
	private static class WorkGiver_Scanner__HasJobOnThing_Patch
	{
		[HarmonyPostfix]
		private static void ClearTempDetour(Pawn pawn)
		{
			// Only deactivate temporary detours (HtcOpportunity, HtcBeforeCarry).
			// Don't clear Puah/PuahOpportunity/PuahBeforeCarry detours — those
			// are active unload jobs whose defHauls cache must persist.
			// The original code unconditionally cleared here, which wiped the
			// cache on every work scan (dozens of times per tick per pawn).
			BaseDetour? detour = detours.GetValueSafe(pawn);
			if (detour != null && (detour.type == DetourType.HtcOpportunity || detour.type == DetourType.HtcBeforeCarry))
			{
				detour.Deactivate();
			}
		}
	}

	[HarmonyPatch(typeof(WorkGiver_ConstructDeliverResources), "ResourceDeliverJobFor")]
	private static class WorkGiver_ConstructDeliverResources__ResourceDeliverJobFor_Patch
	{
		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> BeforeSupplyDetour(IEnumerable<CodeInstruction> _codes, ILGenerator generator, MethodBase __originalMethod)
		{
			Transpiler transpiler = new Transpiler(_codes, __originalMethod);
			int nearbyCallIdx = transpiler.TryFindCodeIndex((CodeInstruction code) => code.Calls(AccessTools.DeclaredMethod(typeof(WorkGiver_ConstructDeliverResources), "FindAvailableNearbyResources")));
			Label afterNearbyLabel = generator.DefineLabel();
			transpiler.codes[nearbyCallIdx + 1].labels.Add(afterNearbyLabel);
			FieldInfo needField = AccessTools.FindIncludingInnerTypes(typeof(WorkGiver_ConstructDeliverResources), (Type type) => AccessTools.DeclaredField(type, "need"));
			int needFieldIdx = transpiler.TryFindCodeIndex(nearbyCallIdx, (CodeInstruction code) => code.LoadsField(needField));
			FieldInfo foundResField = AccessTools.FindIncludingInnerTypes(typeof(WorkGiver_ConstructDeliverResources), (Type type) => AccessTools.DeclaredField(type, "foundRes"));
			int foundResFieldIdx = transpiler.TryFindCodeLastIndex(nearbyCallIdx, (CodeInstruction code) => code.LoadsField(foundResField));
			int num = transpiler.TryFindCodeIndex(nearbyCallIdx, (CodeInstruction code) => code.opcode == OpCodes.Leave);
			object jobVar = transpiler.codes[num - 1].operand;
			transpiler.TryInsertCodes(1, (int i, List<CodeInstruction> codes) => i == nearbyCallIdx, (int i, List<CodeInstruction> codes) => new List<CodeInstruction>
			{
				new CodeInstruction(OpCodes.Ldarg_1),
				new CodeInstruction(codes[needFieldIdx - 1]),
				new CodeInstruction(codes[needFieldIdx]),
				new CodeInstruction(OpCodes.Ldarg_2),
				new CodeInstruction(OpCodes.Castclass, typeof(Thing)),
				new CodeInstruction(codes[foundResFieldIdx - 1]),
				new CodeInstruction(codes[foundResFieldIdx]),
				new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(WorkGiver_ConstructDeliverResources__ResourceDeliverJobFor_Patch), "BeforeSupplyDetour_Job")),
				new CodeInstruction(OpCodes.Stloc_S, jobVar),
				new CodeInstruction(OpCodes.Ldloc_S, jobVar),
				new CodeInstruction(OpCodes.Brfalse_S, afterNearbyLabel),
				new CodeInstruction(OpCodes.Ldloc_S, jobVar),
				new CodeInstruction(OpCodes.Ret)
			});
			return transpiler.GetFinalCodes();
		}

		private static Job BeforeSupplyDetour_Job(Pawn pawn, ThingDefCountClass need, Thing constructible, Thing th)
		{
			if (!settings.Enabled || !settings.HaulBeforeCarry_Supplies || AlreadyHauling(pawn))
			{
				return null;
			}
			if (pawn.WorkTagIsDisabled(WorkTags.ManualDumb | WorkTags.Hauling | WorkTags.AllWork))
			{
				return null;
			}

			var thingList = Traverse.Create<WorkGiver_ConstructDeliverResources>().Field<List<Thing>>("resourcesAvailable").Value;
			var thing = thingList.DefaultIfEmpty().MaxBy((Thing x) => x.stackCount);
			// Thing thing = WorkGiver_ConstructDeliverResources.resourcesAvailable.DefaultIfEmpty().MaxBy((Thing x) => x.stackCount);
			if ((!havePuah || !settings.UsePickUpAndHaulPlus) && thing.stackCount <= need.count)
			{
				return null;
			}
			Job job = BeforeCarryDetour_Job(pawn, constructible.Position, thing ?? th);
			return BaseDetour.CatchLoop_Job(pawn, job, "BeforeSupplyDetour_Job");
		}
	}

	[HarmonyPatch(typeof(JobUtility), "TryStartErrorRecoverJob")]
	private static class JobUtility__TryStartErrorRecoverJob_Patch
	{
		[HarmonyPrefix]
		private static void OfferSupport(Pawn pawn)
		{
			if (RealTime.frameCount == BaseDetour.lastFrameCount && pawn == BaseDetour.lastPawn)
			{
				Log.Warning("[" + mod.Content.Name + "] You're welcome to 'Share logs' to my Discord: https://discord.gg/pnZGQAN \n[" + mod.Content.Name + "] Below \"10 jobs in one tick\" error occurred during " + BaseDetour.lastCallerName + ", but could be from several mods.");
			}
		}
	}

	[HarmonyPatch(typeof(Pawn_JobTracker), "TryOpportunisticJob")]
	private static class Pawn_JobTracker__TryOpportunisticJob_Patch
	{
		private static JobDef[] prepareCaravanJobDefs;

		private static bool IsEnabled()
		{
			return settings.Enabled;
		}

		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> _TryOpportunisticJob(IEnumerable<CodeInstruction> _codes, ILGenerator generator, MethodBase __originalMethod)
		{
			Transpiler transpiler = new Transpiler(_codes, __originalMethod);
			int listerHaulablesIdx = transpiler.TryFindCodeIndex((CodeInstruction code) => code.LoadsField(AccessTools.DeclaredField(typeof(Map), "listerHaulables")));
			Label skipMod = generator.DefineLabel();
			transpiler.TryInsertCodes(-3, (int i, List<CodeInstruction> codes) => i == listerHaulablesIdx, (int i, List<CodeInstruction> codes) => new List<CodeInstruction>
			{
				new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(Pawn_JobTracker__TryOpportunisticJob_Patch), "IsEnabled")),
				new CodeInstruction(OpCodes.Brfalse_S, skipMod),
				new CodeInstruction(OpCodes.Ldarg_0),
				new CodeInstruction(OpCodes.Ldarg_2),
				new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(Pawn_JobTracker__TryOpportunisticJob_Patch), "TryOpportunisticJob")),
				new CodeInstruction(OpCodes.Ret)
			}, bringLabels: true);
			transpiler.codes[transpiler.MatchIdx - 3].labels.Add(skipMod);
			return transpiler.GetFinalCodes();
		}

		private static Job TryOpportunisticJob(Pawn_JobTracker jobTracker, Job job)
		{
			Pawn value = Traverse.Create(jobTracker).Field("pawn").GetValue<Pawn>();
			if (AlreadyHauling(value))
			{
				return null;
			}
			Job job2;
			if (job.def == JobDefOf.DoBill && settings.HaulBeforeCarry_Bills)
			{
				for (int i = 0; i < job.targetQueueB.Count; i++)
				{
					LocalTargetInfo localTargetInfo = job.targetQueueB[i];
					if (
						localTargetInfo.Thing != null && localTargetInfo.Thing.ParentHolder != null &&
						((havePuah && settings.UsePickUpAndHaulPlus) || localTargetInfo.Thing.stackCount > job.countQueue[i]) &&
						HaulAIUtility.PawnCanAutomaticallyHaulFast(value, localTargetInfo.Thing, forced: false)
					)
					{
						job2 = BeforeCarryDetour_Job(value, job.targetA, localTargetInfo.Thing);
						if (job2 != null)
						{
							return BaseDetour.CatchLoop_Job(value, job2, "TryOpportunisticJob");
						}
					}
				}
			}
			if (prepareCaravanJobDefs == null)
			{
				prepareCaravanJobDefs = new JobDef[4]
				{
					JobDefOf.PrepareCaravan_CollectAnimals,
					JobDefOf.PrepareCaravan_GatherAnimals,
					JobDefOf.PrepareCaravan_GatherDownedPawns,
					JobDefOf.PrepareCaravan_GatherItems
				};
			}
			if (prepareCaravanJobDefs.Contains(job.def))
			{
				return null;
			}
			if (AmBleeding(value))
			{
				return null;
			}
			BaseDetour valueSafe = detours.GetValueSafe(value);
			if ((object)valueSafe != null && valueSafe.opportunity.puah.unloadedTick > 0 && RealTime.frameCount - valueSafe.opportunity.puah.unloadedTick <= 5)
			{
				return null;
			}
			LocalTargetInfo jobTarget = ((job.def != JobDefOf.DoBill) ? job.targetA : (job.targetQueueB?.FirstOrDefault() ?? job.targetA));
			job2 = Opportunity_Job(value, jobTarget);
			return BaseDetour.CatchLoop_Job(value, job2, "TryOpportunisticJob");
		}
	}

	private enum CanHaulResult
	{
		RangeFail,
		HardFail,
		FullStop,
		Success
	}

	private struct MaxRanges
	{
		[TweakValue("WhileYoureUp.Opportunity", 1.1f, 3f)]
		public static float heuristicRangeExpandFactor = 2f;

		public int expandCount;

		public float startToThing;

		public float startToThingPctOrigTrip;

		public float storeToJob;

		public float storeToJobPctOrigTrip;

		public void Reset()
		{
			expandCount = 0;
			startToThing = settings.Opportunity_MaxStartToThing;
			startToThingPctOrigTrip = settings.Opportunity_MaxStartToThingPctOrigTrip;
			storeToJob = settings.Opportunity_MaxStoreToJob;
			storeToJobPctOrigTrip = settings.Opportunity_MaxStoreToJobPctOrigTrip;
		}

		public static MaxRanges operator *(MaxRanges maxRanges, float multiplier)
		{
			maxRanges.expandCount++;
			maxRanges.startToThing *= multiplier;
			maxRanges.startToThingPctOrigTrip *= multiplier;
			maxRanges.storeToJob *= multiplier;
			maxRanges.storeToJobPctOrigTrip *= multiplier;
			return maxRanges;
		}
	}

	[HarmonyPatch]
	private static class Puah_WorkGiver_HaulToInventory__HasJobOnThing_Patch
	{
		private static bool Prepare()
		{
			return havePuah;
		}

		private static MethodBase TargetMethod()
		{
			return PuahMethod_WorkGiver_HaulToInventory_HasJobOnThing;
		}

		private static void Prefix(MethodBase __originalMethod)
		{
			PushHtiMethod(__originalMethod);
		}

		private static void Postfix()
		{
			PopHtiMethod();
		}
	}

	[HarmonyPatch]
	private static class Puah_WorkGiver_HaulToInventory__AllocateThingAtCell_Patch
	{
		private static bool Prepare()
		{
			return havePuah;
		}

		private static MethodBase TargetMethod()
		{
			return PuahMethod_WorkGiver_HaulToInventory_AllocateThingAt;
		}

		private static void Prefix(MethodBase __originalMethod)
		{
			PushHtiMethod(__originalMethod);
		}

		private static void Postfix()
		{
			PopHtiMethod();
		}
	}

	[HarmonyPatch]
	private static class Puah_StorageSettings_Priority_Patch
	{
		private static bool Prepare()
		{
			return havePuah;
		}

		private static MethodBase TargetMethod()
		{
			return AccessTools.DeclaredPropertyGetter(typeof(StorageSettings), "Priority");
		}

		[HarmonyPostfix]
		private static void GetReducedPriority(StorageSettings __instance, ref StoragePriority __result)
		{
			if (__instance == reducedPriorityStore && (int)__result > 0)
			{
				__result--;
			}
		}
	}

	[HarmonyPatch]
	private static class Puah_ListerHaulables_ThingsPotentiallyNeedingHauling_Patch
	{
		private static bool Prepare()
		{
			return havePuah;
		}

		private static MethodBase TargetMethod()
		{
			return AccessTools.DeclaredMethod(typeof(ListerHaulables), "ThingsPotentiallyNeedingHauling");
		}

		[HarmonyPostfix]
		private static void IncludeThingsInReducedPriorityStore(ref ICollection<Thing> __result)
		{
			if (!thingsInReducedPriorityStore.NullOrEmpty())
			{
				thingsInReducedPriorityStore.ForEach(__result.Add);
			}
		}
	}

	[HarmonyPatch]
	private static class Puah_JobDriver_UnloadYourHauledInventory__FirstUnloadableThing_Patch
	{
		private static bool Prepare()
		{
			return havePuah;
		}

		private static MethodBase TargetMethod()
		{
			return PuahMethod_JobDriver_UnloadYourHauledInventory_FirstUnloadableThing;
		}

		[HarmonyPrefix]
		private static bool DetourAwareFirstUnloadableThing(ref ThingCount __result, Pawn pawn)
		{
			if (!settings.Enabled || !settings.UsePickUpAndHaulPlus)
			{
				return CodeOptimist.Patch.Continue();
			}
			// Use cached MethodInfo instead of Traverse to avoid reflection
			// overhead on every tick.
			var comp = (ThingComp)PuahMethod_CompHauledToInventory_GetComp.Invoke(pawn, null);
			HashSet<Thing> value = (HashSet<Thing>)PuahMethod_CompHauledToInventory_GetHashSet.Invoke(comp, null);
			if (value.Count == 0)
			{
				return CodeOptimist.Patch.Halt(__result = default(ThingCount));
			}
			BaseDetour detour = SetOrAddDetour(pawn, DetourType.ExistingElsePuah);
			// Single pass: compute GetDefHaul for each thing once and
			// materialize the valid results. The original code called
			// GetDefHaul twice (two Linq passes over the same set),
			// doubling the storage cell lookup cost.
			var allDefHauls = new List<(Thing thing, IntVec3 storeCell)>(value.Count);
			foreach (var t in value)
			{
				var dh = GetDefHaul(t);
				if (dh.storeCell.IsValid)
					allDefHauls.Add(dh);
			}
			(Thing, IntVec3) tuple = allDefHauls.Count > 0
				? allDefHauls.MinBy(x => x.storeCell.DistanceToSquared(pawn.Position))
				: default;
			SlotGroup closestSlotGroup = (tuple.Item2.IsValid ? tuple.Item2.GetSlotGroup(pawn.Map) : null);
			Thing thing;
			if (closestSlotGroup == null)
			{
				(thing, _) = tuple;
			}
			else
			{
				// Reuse the materialized list instead of a second GetDefHaul pass
				thing = allDefHauls
					.Where(x => x.storeCell.GetSlotGroup(pawn.Map) == closestSlotGroup)
					.DefaultIfEmpty()
					.MinBy(x => (index: x.thing?.def.FirstThingCategory?.index, defName: x.thing?.def.defName)).thing;
			}
			Thing firstThingToUnload = thing;
			if (firstThingToUnload == null)
			{
				firstThingToUnload = value.MinBy((Thing t) => (index: t.def.FirstThingCategory?.index, defName: t.def.defName));
			}
			// Manual check instead of Linq Intersect+Contains to avoid
			// allocating intermediate collections every tick.
			if (!pawn.inventory.innerContainer.Contains(firstThingToUnload))
			{
				value.Remove(firstThingToUnload);
				Thing thing2 = pawn.inventory.innerContainer.FirstOrDefault((Thing t) => t.def == firstThingToUnload.def);
				if (thing2 != null)
				{
					return CodeOptimist.Patch.Halt(__result = new ThingCount(thing2, thing2.stackCount));
				}
			}
			return CodeOptimist.Patch.Halt(__result = new ThingCount(firstThingToUnload, firstThingToUnload.stackCount));
			(Thing thing, IntVec3 storeCell) GetDefHaul(Thing thing3)
			{
				if (detour.puah.defHauls.TryGetValue(thing3.def, out var value2))
				{
					return (thing: thing3, storeCell: value2);
				}
				if (TryFindBestBetterStoreCellFor_MidwayToTarget(thing3, detour.opportunity.jobTarget, detour.beforeCarry.carryTarget, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing3), pawn.Faction, out value2, needAccurateResult: false))
				{
					detour.puah.defHauls.Add(thing3.def, value2);
				}
				return (thing: thing3, storeCell: value2);
			}
		}
	}

	[HarmonyPatch]
	private static class Dialog_ModSettings__Dialog_ModSettings_Patch
	{
		private static MethodBase TargetMethod()
		{
			if (haveHugs)
			{
				return AccessTools.DeclaredConstructor(HugsType_Dialog_VanillaModSettings, new Type[1] { typeof(Verse.Mod) });
			}
			return AccessTools.DeclaredConstructor(typeof(Dialog_ModSettings), new Type[1] { typeof(Verse.Mod) });
		}

		[HarmonyPostfix]
		private static void SyncDrawSettingToVanilla()
		{
			settings.DrawSpecialHauls = DebugViewSettings.drawOpportunisticJobs;
		}
	}

	[HarmonyPatch]
	private static class Dialog_ModSettings__DoWindowContents_Patch
	{
		private static Verse.Mod csMod;

		private static MethodBase TargetMethod()
		{
			if (haveHugs)
			{
				return AccessTools.DeclaredMethod(HugsType_Dialog_VanillaModSettings, "DoWindowContents");
			}
			return AccessTools.DeclaredMethod(typeof(Dialog_ModSettings), "DoWindowContents");
		}

		[HarmonyPostfix]
		private static void CheckCommonSenseSetting(object __instance)
		{
			if (haveCommonSense && settings.HaulBeforeCarry_Bills && (bool)CsField_Settings_HaulingOverBills.GetValue(null))
			{
				if (csMod == null)
				{
					csMod = LoadedModManager.GetMod(CsType_CommonSense);
				}
				object value = SettingsCurModField.GetValue(__instance);
				if (value == mod)
				{
					CsField_Settings_HaulingOverBills.SetValue(null, false);
					csMod.WriteSettings();
					Messages.Message("[" + mod.Content.Name + "] Unticked setting in CommonSense: \"haul ingredients for a bill\". (Can't use both.)", MessageTypeDefOf.SilentInput, historical: false);
				}
				else if (value == csMod)
				{
					settings.HaulBeforeCarry_Bills = false;
					Messages.Message("[" + mod.Content.Name + "] Unticked setting in While You're Up: \"Haul extra bill ingredients closer\". (Can't use both.)", MessageTypeDefOf.SilentInput, historical: false);
				}
			}
		}
	}

	public class Listing_TreeModFilter : Listing_TreeNonThingFilter
	{
		public Listing_TreeModFilter(ModFilter filter, ModFilter parentFilter, IEnumerable<ThingDef> forceHiddenDefs, IEnumerable<SpecialThingFilterDef> forceHiddenFilters, List<ThingDef> suppressSmallVolumeTags, QuickSearchFilter searchFilter)
			: base(filter, parentFilter, forceHiddenDefs, forceHiddenFilters, suppressSmallVolumeTags, searchFilter)
		{
		}
	}

	public class ModFilter : NonThingFilter
	{
	}

	[StaticConstructorOnStartup]
	public static class SettingsWindow
	{
		private enum Tab
		{
			Opportunity,
			OpportunityAdvanced,
			BeforeCarryDetour,
			PickUpAndHaul
		}

		private static Vector2 opportunityScrollPosition;

		private static Listing_TreeModFilter opportunityTreeFilter;

		private static readonly QuickSearchFilter opportunitySearchFilter;

		private static readonly QuickSearchWidget opportunitySearchWidget;

		private static readonly ModFilter opportunityDummyFilter;

		private static Vector2 hbcScrollPosition;

		private static Listing_TreeModFilter hbcTreeFilter;

		private static readonly QuickSearchFilter hbcSearchFilter;

		private static readonly QuickSearchWidget hbcSearchWidget;

		private static readonly ModFilter hbcDummyFilter;

		private static readonly ThingCategoryDef storageBuildingCategoryDef;

		private static readonly List<TabRecord> tabsList;

		private static Tab tab;

		static SettingsWindow()
		{
			opportunitySearchFilter = new QuickSearchFilter();
			opportunitySearchWidget = new QuickSearchWidget();
			opportunityDummyFilter = new ModFilter();
			hbcSearchFilter = new QuickSearchFilter();
			hbcSearchWidget = new QuickSearchWidget();
			hbcDummyFilter = new ModFilter();
			tabsList = new List<TabRecord>(4);
			tab = Tab.Opportunity;
			if (haveCommonSense)
			{
				if (settings.HaulBeforeCarry_Bills_NeedsInitForCs)
				{
					CsField_Settings_HaulingOverBills.SetValue(null, false);
					settings.HaulBeforeCarry_Bills = true;
					settings.HaulBeforeCarry_Bills_NeedsInitForCs = false;
				}
				else if ((bool)CsField_Settings_HaulingOverBills.GetValue(null))
				{
					settings.HaulBeforeCarry_Bills = false;
				}
			}
			using (NonThingFilter_LoadingContext nonThingFilter_LoadingContext = new NonThingFilter_LoadingContext())
			{
				try
				{
					settings.opportunityBuildingFilter = ScribeExtractor.SaveableFromNode<ModFilter>(settings.opportunityBuildingFilterXmlNode, null);
					settings.hbcBuildingFilter = ScribeExtractor.SaveableFromNode<ModFilter>(settings.hbcBuildingFilterXmlNode, null);
				}
				catch (Exception)
				{
					nonThingFilter_LoadingContext.Dispose();
					throw;
				}
			}
			hbcSearchWidget.filter = hbcSearchFilter;
			List<Type> storageBuildingTypes = typeof(Building_Storage).AllSubclassesNonAbstract();
			storageBuildingTypes.Add(typeof(Building_Storage));
			storageBuildingCategoryDef = new ThingCategoryDef();
			List<ThingDef> source = DefDatabase<ThingDef>.AllDefsListForReading.Where((ThingDef x) => storageBuildingTypes.Contains(x.thingClass)).ToList();
			foreach (ModContentPack storageMod in source.Select((ThingDef x) => x.modContentPack).Distinct())
			{
				if (storageMod != null)
				{
					ThingCategoryDef thingCategoryDef = new ThingCategoryDef
					{
						label = storageMod.Name
					};
					storageBuildingCategoryDef.childCategories.Add(thingCategoryDef);
					thingCategoryDef.childThingDefs.AddRange(from x in source
															 where x.modContentPack == storageMod
															 select (x));
					thingCategoryDef.PostLoad();
					thingCategoryDef.ResolveReferences();
				}
			}
			storageBuildingCategoryDef.PostLoad();
			storageBuildingCategoryDef.ResolveReferences();
			ResetFilters();
			if (settings.opportunityBuildingFilter == null)
			{
				settings.opportunityBuildingFilter = new ModFilter();
				settings.opportunityBuildingFilter?.CopyAllowancesFrom(settings.opportunityDefaultBuildingFilter);
			}
			if (settings.hbcBuildingFilter == null)
			{
				settings.hbcBuildingFilter = new ModFilter();
				settings.hbcBuildingFilter?.CopyAllowancesFrom(settings.hbcDefaultBuildingFilter);
			}
		}

		private static void ResetFilters()
		{
			foreach (ThingCategoryDef modCategoryDef in storageBuildingCategoryDef.childCategories)
			{
				ModContentPack modContentPack = LoadedModManager.RunningModsListForReading.FirstOrDefault((ModContentPack x) => x.Name == modCategoryDef.label);
				modCategoryDef.treeNode.SetOpen(1, val: false);
				if (modContentPack?.PackageId == "ludeon.rimworld")
				{
					modCategoryDef.treeNode.SetOpen(1, val: true);
				}
				settings.opportunityDefaultBuildingFilter.SetAllow(modCategoryDef, allow: true);
				if (modContentPack?.PackageId == "lwm.deepstorage")
				{
					settings.opportunityDefaultBuildingFilter.SetAllow(modCategoryDef, allow: false);
				}
				settings.hbcDefaultBuildingFilter.SetAllow(modCategoryDef, allow: false);
				switch (modContentPack?.PackageId)
				{
					case "jangodsoul.simplestorage.ref":
					case "ogliss.thewhitecrayon.quarry":
					case "rimfridge.kv.rw":
					case "ludeon.rimworld":
					case "lwm.deepstorage":
					case "mlie.displaycases":
					case "mlie.eggincubator":
					case "sixdd.littlestorage2":
					case "mlie.extendedstorage":
					case "mlie.tobesdiningroom":
					case "mlie.fireextinguisher":
					case "solaris.furniturebase":
					case "skullywag.extendedstorage":
					case "vanillaexpanded.vfespacer":
					case "buddy1913.expandedstorageboxes":
					case "im.skye.rimworld.deepstorageplus":
					case "jangodsoul.simplestorage":
					case "mlie.functionalvanillaexpandedprops":
					case "primitivestorage.velcroboy333":
					case "proxyer.smallshelf":
					case "vanillaexpanded.vfecore":
					case "vanillaexpanded.vfeart":
					case "vanillaexpanded.vfefarming":
					case "vanillaexpanded.vfesecurity":
						settings.hbcDefaultBuildingFilter.SetAllow(modCategoryDef, allow: true);
						break;
				}
			}
		}

		public static void DoWindowContents(Rect windowRect)
		{
			Listing_Standard listing_Standard = new Listing_Standard
			{
				ColumnWidth = (float)Math.Round((windowRect.width - 34f) / 3f)
			};
			listing_Standard.Begin(windowRect);
			listing_Standard.DrawBool(ref settings.Enabled, "Enabled");
			listing_Standard.NewColumn();
			listing_Standard.DrawBool(ref settings.DrawSpecialHauls, "DrawSpecialHauls");
			listing_Standard.NewColumn();
			if (havePuah)
			{
				listing_Standard.DrawBool(ref settings.UsePickUpAndHaulPlus, "UsePickUpAndHaulPlus");
				if (tab == Tab.PickUpAndHaul && !settings.UsePickUpAndHaulPlus)
				{
					tab = Tab.BeforeCarryDetour;
				}
			}
			else
			{
				bool checkOn = false;
				listing_Standard.CheckboxLabeled("PickUpAndHaul_Missing".ModTranslate(), ref checkOn, "PickUpAndHaul_Tooltip".ModTranslate());
			}
			tabsList.Clear();
			tabsList.Add(new TabRecord("Opportunity_Tab".ModTranslate(), delegate
			{
				tab = Tab.Opportunity;
			}, tab == Tab.Opportunity));
			if (settings.Opportunity_TweakVanilla)
			{
				tabsList.Add(new TabRecord("OpportunityAdvanced_Tab".ModTranslate(), delegate
				{
					tab = Tab.OpportunityAdvanced;
				}, tab == Tab.OpportunityAdvanced));
			}
			tabsList.Add(new TabRecord("HaulBeforeCarry_Tab".ModTranslate(), delegate
			{
				tab = Tab.BeforeCarryDetour;
			}, tab == Tab.BeforeCarryDetour));
			if (havePuah && settings.UsePickUpAndHaulPlus)
			{
				tabsList.Add(new TabRecord("PickUpAndHaulPlus_Tab".ModTranslate(), delegate
				{
					tab = Tab.PickUpAndHaul;
				}, tab == Tab.PickUpAndHaul));
			}
			Rect rect = windowRect.AtZero();
			rect.yMin += listing_Standard.MaxColumnHeightSeen;
			rect.yMin += 42f;
			rect.height -= 42f;
			Widgets.DrawMenuSection(rect);
			TabDrawer.DrawTabs(rect, tabsList);
			Rect innerRect = rect.GetInnerRect();
			Rect rect3;
			switch (tab)
			{
				case Tab.Opportunity:
					{
						Listing_Standard listing_Standard4 = new Listing_Standard
						{
							ColumnWidth = (float)Math.Round((innerRect.width - 17f) / 2f)
						};
						listing_Standard4.Begin(innerRect);
						listing_Standard4.Label("Opportunity_Intro".ModTranslate(), -1f);
						listing_Standard4.Gap();
						using (new DrawContext
						{
							LabelPct = 0.25f
						})
						{
							listing_Standard4.DrawEnum(settings.Opportunity_PathChecker, "Opportunity_PathChecker", delegate (Settings.PathCheckerEnum val)
							{
								settings.Opportunity_PathChecker = val;
							}, Text.LineHeight * 2f);
						}
						listing_Standard4.Gap();
						listing_Standard4.DrawBool(ref settings.Opportunity_TweakVanilla, "Opportunity_TweakVanilla");
						listing_Standard4.NewColumn();
						listing_Standard4.Label("Opportunity_Tab".ModTranslate(), -1f);
						listing_Standard4.GapLine();
						bool value = !settings.Opportunity_AutoBuildings;
						listing_Standard4.DrawBool(ref value, "Opportunity_AutoBuildings");
						settings.Opportunity_AutoBuildings = !value;
						listing_Standard4.Gap(4f);
						opportunitySearchWidget.OnGUI(listing_Standard4.GetRect(24f));
						listing_Standard4.Gap(4f);
						Rect rect2 = listing_Standard4.GetRect(innerRect.height - listing_Standard4.CurHeight - Text.LineHeight * 2f);
						float num = 20f;
						rect3 = new Rect(0f, 0f, rect2.width - num, opportunityTreeFilter?.CurHeight ?? 10000f);
						Widgets.BeginScrollView(rect2, ref opportunityScrollPosition, rect3);
						if (settings.Opportunity_AutoBuildings)
						{
							opportunityDummyFilter.CopyAllowancesFrom(settings.opportunityDefaultBuildingFilter);
						}
						opportunityTreeFilter = new Listing_TreeModFilter(settings.Opportunity_AutoBuildings ? opportunityDummyFilter : settings.opportunityBuildingFilter, null, null, null, null, opportunitySearchFilter);
						opportunityTreeFilter.Begin(rect3);
						opportunityTreeFilter.ListCategoryChildren(storageBuildingCategoryDef.treeNode, 1, null, rect3);
						opportunityTreeFilter.End();
						Widgets.EndScrollView();
						listing_Standard4.GapLine();
						listing_Standard4.DrawBool(ref settings.Opportunity_ToStockpiles, "Opportunity_ToStockpiles");
						listing_Standard4.End();
						break;
					}
				case Tab.OpportunityAdvanced:
					{
						float labelPct = 0.75f;
						Listing_Standard listing_Standard5 = new Listing_Standard();
						listing_Standard5.Begin(innerRect);
						listing_Standard5.Label("OpportunityAdvanced_Text1".ModTranslate(), -1f);
						using (new DrawContext
						{
							TextAnchor = TextAnchor.MiddleRight,
							LabelPct = labelPct
						})
						{
							listing_Standard5.DrawPercent(ref settings.Opportunity_MaxNewLegsPctOrigTrip, "Opportunity_MaxNewLegsPctOrigTrip");
							listing_Standard5.DrawPercent(ref settings.Opportunity_MaxTotalTripPctOrigTrip, "Opportunity_MaxTotalTripPctOrigTrip");
						}
						listing_Standard5.Gap();
						listing_Standard5.GapLine();
						listing_Standard5.Gap();
						listing_Standard5.Label("OpportunityAdvanced_Text2".ModTranslate(), -1f);
						using (new DrawContext
						{
							TextAnchor = TextAnchor.MiddleRight,
							LabelPct = labelPct
						})
						{
							listing_Standard5.DrawFloat(ref settings.Opportunity_MaxStartToThing, "Opportunity_MaxStartToThing");
							listing_Standard5.DrawFloat(ref settings.Opportunity_MaxStoreToJob, "Opportunity_MaxStoreToJob");
							listing_Standard5.DrawPercent(ref settings.Opportunity_MaxStartToThingPctOrigTrip, "Opportunity_MaxStartToThingPctOrigTrip");
							listing_Standard5.DrawPercent(ref settings.Opportunity_MaxStoreToJobPctOrigTrip, "Opportunity_MaxStoreToJobPctOrigTrip");
						}
						listing_Standard5.Gap();
						listing_Standard5.GapLine();
						listing_Standard5.Gap();
						listing_Standard5.Label("OpportunityAdvanced_Text3".ModTranslate(), -1f);
						using (new DrawContext
						{
							TextAnchor = TextAnchor.MiddleRight,
							LabelPct = labelPct
						})
						{
							listing_Standard5.DrawInt(ref settings.Opportunity_MaxStartToThingRegionLookCount, "Opportunity_MaxStartToThingRegionLookCount");
							listing_Standard5.DrawInt(ref settings.Opportunity_MaxStoreToJobRegionLookCount, "Opportunity_MaxStoreToJobRegionLookCount");
						}
						listing_Standard5.End();
						break;
					}
				case Tab.BeforeCarryDetour:
					{
						Listing_Standard listing_Standard3 = new Listing_Standard
						{
							ColumnWidth = (float)Math.Round((innerRect.width - 17f) / 2f)
						};
						listing_Standard3.Begin(innerRect);
						listing_Standard3.Label("HaulBeforeCarry_Intro".ModTranslate(), -1f);
						listing_Standard3.DrawBool(ref settings.HaulBeforeCarry_Supplies, "HaulBeforeCarry_Supplies");
						listing_Standard3.DrawBool(ref settings.HaulBeforeCarry_Bills, "HaulBeforeCarry_Bills");
						if (havePuah && settings.UsePickUpAndHaulPlus)
						{
							listing_Standard3.Gap();
							listing_Standard3.Label("HaulBeforeCarry_EqualPriority".ModTranslate(), -1f);
							listing_Standard3.DrawBool(ref settings.HaulBeforeCarry_ToEqualPriority, "HaulBeforeCarry_ToEqualPriority");
						}
						listing_Standard3.NewColumn();
						listing_Standard3.Label("HaulBeforeCarry_Tab".ModTranslate(), -1f);
						listing_Standard3.GapLine();
						bool value = !settings.HaulBeforeCarry_AutoBuildings;
						listing_Standard3.DrawBool(ref value, "HaulBeforeCarry_AutoBuildings");
						settings.HaulBeforeCarry_AutoBuildings = !value;
						listing_Standard3.Gap(4f);
						hbcSearchWidget.OnGUI(listing_Standard3.GetRect(24f));
						listing_Standard3.Gap(4f);
						Rect rect2 = listing_Standard3.GetRect(innerRect.height - listing_Standard3.CurHeight - Text.LineHeight * 2f);
						float num = 20f;
						rect3 = new Rect(0f, 0f, rect2.width - num, hbcTreeFilter?.CurHeight ?? 10000f);
						Widgets.BeginScrollView(rect2, ref hbcScrollPosition, rect3);
						if (settings.HaulBeforeCarry_AutoBuildings)
						{
							hbcDummyFilter.CopyAllowancesFrom(settings.hbcDefaultBuildingFilter);
						}
						hbcTreeFilter = new Listing_TreeModFilter(settings.HaulBeforeCarry_AutoBuildings ? hbcDummyFilter : settings.hbcBuildingFilter, null, null, null, null, hbcSearchFilter);
						hbcTreeFilter.Begin(rect3);
						hbcTreeFilter.ListCategoryChildren(storageBuildingCategoryDef.treeNode, 1, null, rect3);
						hbcTreeFilter.End();
						Widgets.EndScrollView();
						listing_Standard3.GapLine();
						listing_Standard3.DrawBool(ref settings.HaulBeforeCarry_ToStockpiles, "HaulBeforeCarry_ToStockpiles");
						listing_Standard3.End();
						break;
					}
				case Tab.PickUpAndHaul:
					{
						Listing_Standard listing_Standard2 = new Listing_Standard();
						listing_Standard2.ColumnWidth = (float)Math.Round((innerRect.width - 17f) / 2f);
						listing_Standard2.Begin(innerRect);
						listing_Standard2.Label("PickUpAndHaulPlus_Text1".ModTranslate(), -1f);
						listing_Standard2.GapLine();
						listing_Standard2.Gap();
						listing_Standard2.Label("PickUpAndHaulPlus_Text2".ModTranslate(), -1f);
						listing_Standard2.End();
						break;
					}
			}
			Rect rect4 = windowRect.AtZero();
			rect4.yMin += rect.yMax;
			listing_Standard.Begin(rect4);
			listing_Standard.Gap(6f);
			if (Widgets.ButtonText(listing_Standard.GetRect(30f), "RestoreToDefaultSettings".Translate()))
			{
				settings.ExposeData();
				opportunitySearchWidget.Reset();
				hbcSearchWidget.Reset();
				ResetFilters();
			}
			listing_Standard.Gap(6f);
			listing_Standard.End();
			listing_Standard.End();
		}
	}

	internal class Settings : ModSettings
	{
		public enum PathCheckerEnum
		{
			Vanilla,
			Default,
			Pathfinding
		}

		public bool Enabled;

		public bool UsePickUpAndHaulPlus;

		public bool DrawSpecialHauls;

		public PathCheckerEnum Opportunity_PathChecker;

		public bool Opportunity_TweakVanilla;

		public bool Opportunity_ToStockpiles;

		public bool Opportunity_AutoBuildings;

		public float Opportunity_MaxStartToThing;

		public float Opportunity_MaxStartToThingPctOrigTrip;

		public float Opportunity_MaxStoreToJob;

		public float Opportunity_MaxStoreToJobPctOrigTrip;

		public float Opportunity_MaxTotalTripPctOrigTrip;

		public float Opportunity_MaxNewLegsPctOrigTrip;

		public int Opportunity_MaxStartToThingRegionLookCount;

		public int Opportunity_MaxStoreToJobRegionLookCount;

		internal readonly ModFilter opportunityDefaultBuildingFilter = new ModFilter();

		internal ModFilter opportunityBuildingFilter;

		internal XmlNode opportunityBuildingFilterXmlNode;

		public bool HaulBeforeCarry_Supplies;

		public bool HaulBeforeCarry_Bills;

		public bool HaulBeforeCarry_Bills_NeedsInitForCs;

		public bool HaulBeforeCarry_ToEqualPriority;

		public bool HaulBeforeCarry_ToStockpiles;

		public bool HaulBeforeCarry_AutoBuildings;

		internal readonly ModFilter hbcDefaultBuildingFilter = new ModFilter();

		internal ModFilter hbcBuildingFilter;

		internal XmlNode hbcBuildingFilterXmlNode;

		public ModFilter Opportunity_BuildingFilter
		{
			get
			{
				if (!Opportunity_AutoBuildings)
				{
					return opportunityBuildingFilter;
				}
				return opportunityDefaultBuildingFilter;
			}
		}

		public ModFilter HaulBeforeCarry_BuildingFilter
		{
			get
			{
				if (!HaulBeforeCarry_AutoBuildings)
				{
					return hbcBuildingFilter;
				}
				return hbcDefaultBuildingFilter;
			}
		}

		public override void ExposeData()
		{
			if (Scribe.mode != LoadSaveMode.Inactive)
			{
				foundConfig = true;
			}
			Look<bool>(ref Enabled, "Enabled", defaultValue: true);
			Look<bool>(ref DrawSpecialHauls, "DrawSpecialHauls", defaultValue: false);
			Look<bool>(ref UsePickUpAndHaulPlus, "UsePickUpAndHaulPlus", defaultValue: true);
			Look<PathCheckerEnum>(ref Opportunity_PathChecker, "Opportunity_PathChecker", PathCheckerEnum.Default);
			Look<bool>(ref Opportunity_TweakVanilla, "Opportunity_TweakVanilla", defaultValue: false);
			Look<float>(ref Opportunity_MaxStartToThing, "Opportunity_MaxStartToThing", 30f);
			Look<float>(ref Opportunity_MaxStartToThingPctOrigTrip, "Opportunity_MaxStartToThingPctOrigTrip", 0.5f);
			Look<float>(ref Opportunity_MaxStoreToJob, "Opportunity_MaxStoreToJob", 50f);
			Look<float>(ref Opportunity_MaxStoreToJobPctOrigTrip, "Opportunity_MaxStoreToJobPctOrigTrip", 0.6f);
			Look<float>(ref Opportunity_MaxTotalTripPctOrigTrip, "Opportunity_MaxTotalTripPctOrigTrip", 1.7f);
			Look<float>(ref Opportunity_MaxNewLegsPctOrigTrip, "Opportunity_MaxNewLegsPctOrigTrip", 1f);
			Look<int>(ref Opportunity_MaxStartToThingRegionLookCount, "Opportunity_MaxStartToThingRegionLookCount", 25);
			Look<int>(ref Opportunity_MaxStoreToJobRegionLookCount, "Opportunity_MaxStoreToJobRegionLookCount", 25);
			Look<bool>(ref Opportunity_ToStockpiles, "Opportunity_ToStockpiles", defaultValue: true);
			Look<bool>(ref Opportunity_AutoBuildings, "Opportunity_AutoBuildings", defaultValue: true);
			Look<bool>(ref HaulBeforeCarry_Supplies, "HaulBeforeCarry_Supplies", defaultValue: true);
			Look<bool>(ref HaulBeforeCarry_Bills, "HaulBeforeCarry_Bills", defaultValue: true);
			Look<bool>(ref HaulBeforeCarry_Bills_NeedsInitForCs, "HaulBeforeCarry_Bills_NeedsInitForCs", defaultValue: true);
			Look<bool>(ref HaulBeforeCarry_ToEqualPriority, "HaulBeforeCarry_ToEqualPriority", defaultValue: true);
			Look<bool>(ref HaulBeforeCarry_ToStockpiles, "HaulBeforeCarry_ToStockpiles", defaultValue: true);
			Look<bool>(ref HaulBeforeCarry_AutoBuildings, "HaulBeforeCarry_AutoBuildings", defaultValue: true);
			if (Scribe.mode == LoadSaveMode.Saving)
			{
				Scribe_Deep.Look(ref hbcBuildingFilter, "hbcBuildingFilter");
				Scribe_Deep.Look(ref opportunityBuildingFilter, "opportunityBuildingFilter");
			}
			if (Scribe.mode == LoadSaveMode.LoadingVars)
			{
				hbcBuildingFilterXmlNode = Scribe.loader.curXmlParent["hbcBuildingFilter"];
				opportunityBuildingFilterXmlNode = Scribe.loader.curXmlParent["opportunityBuildingFilter"];
			}
			LoadSaveMode mode = Scribe.mode;
			if (mode - 1 <= LoadSaveMode.Saving)
			{
				DebugViewSettings.drawOpportunisticJobs = DrawSpecialHauls;
			}
			static void Look<T>(ref T value, string label, T defaultValue)
			{
				if (Scribe.mode == LoadSaveMode.Inactive)
				{
					value = defaultValue;
				}
				Scribe_Values.Look(ref value, label, defaultValue);
			}
		}
	}

	[HarmonyPatch]
	private static class Puah_WorkGiver_HaulToInventory__TryFindBestBetterStoreCellFor_Patch
	{
		private static bool Prepare()
		{
			return havePuah;
		}

		private static MethodBase TargetMethod()
		{
			return PuahMethod_WorkGiver_HaulToInventory_TryFindBestBetterStoreCellFor;
		}

		[HarmonyPrefix]
		private static bool Use_DetourAware_TryFindStore(ref bool __result, Thing thing, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, ref IntVec3 foundCell)
		{
			if (!settings.Enabled || !settings.UsePickUpAndHaulPlus)
			{
				return CodeOptimist.Patch.Continue();
			}
			__result = StoreUtility.TryFindBestBetterStoreCellFor(thing, carrier, map, currentPriority, faction, out foundCell);
			return CodeOptimist.Patch.Halt();
		}
	}

	[HarmonyPriority(500)]
	[HarmonyPatch]
	private static class StoreUtility__TryFindBestBetterStoreCellFor_Patch
	{
		private static bool Prepare()
		{
			return havePuah;
		}

		private static MethodBase TargetMethod()
		{
			return AccessTools.DeclaredMethod(typeof(StoreUtility), "TryFindBestBetterStoreCellFor");
		}

		[HarmonyPrefix]
		private static bool DetourAware_TryFindStore(ref bool __result, Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, bool needAccurateResult)
		{
			foundCell = IntVec3.Invalid;
			if (carrier == null || !settings.Enabled || !settings.UsePickUpAndHaulPlus)
			{
				return CodeOptimist.Patch.Continue();
			}
			bool flag = carrier.CurJobDef == DefDatabase<JobDef>.GetNamed("UnloadYourHauledInventory");
			if (!puahToInventoryCallStack.Any() && !flag)
			{
				return CodeOptimist.Patch.Continue();
			}
			bool flag2 = !flag;
			HashSet<IntVec3> hashSet = (puahToInventoryCallStack.Contains(PuahMethod_WorkGiver_HaulToInventory_AllocateThingAt) ? ((HashSet<IntVec3>)PuahField_WorkGiver_HaulToInventory_SkipCells.GetValue(null)) : null);
			if (flag2)
			{
				if (puahStoreCellCache.Count == 0)
				{
					puahStoreCellCache.AddRange(opportunityStoreCellCache);
				}
				if (!puahStoreCellCache.TryGetValue(t, out foundCell))
				{
					foundCell = IntVec3.Invalid;
				}
				if (foundCell.IsValid && hashSet != null)
				{
					if (hashSet.Contains(foundCell))
					{
						foundCell = IntVec3.Invalid;
					}
					else
					{
						hashSet.Add(foundCell);
					}
				}
			}
			BaseDetour detour = detours.GetValueSafe(carrier);
			LocalTargetInfo opportunityTarget = detour?.opportunity.jobTarget ?? LocalTargetInfo.Invalid;
			LocalTargetInfo beforeCarryTarget = detour?.beforeCarry.carryTarget ?? LocalTargetInfo.Invalid;
			if (!foundCell.IsValid && !TryFindBestBetterStoreCellFor_MidwayToTarget(t, opportunityTarget, beforeCarryTarget, carrier, map, currentPriority, faction, out foundCell, !puahToInventoryCallStack.Contains(PuahMethod_WorkGiver_HaulToInventory_HasJobOnThing) && (opportunityTarget.IsValid || beforeCarryTarget.IsValid) && Find.TickManager.CurTimeSpeed == TimeSpeed.Normal, hashSet))
			{
				return CodeOptimist.Patch.Halt(__result = false);
			}
			if (flag2)
			{
				puahStoreCellCache.SetOrAdd(t, foundCell);
			}
			IntVec3 storeCell;
			IntVec3 newStoreCell;
			if (flag)
			{
				if (!dumpIfStoreFilledAndAltsInopportune && !DebugViewSettings.drawOpportunisticJobs)
				{
					return CodeOptimist.Patch.Halt(__result = true);
				}
				BaseDetour baseDetour = detour;
				if ((object)baseDetour == null || baseDetour.type != DetourType.PuahOpportunity)
				{
					return CodeOptimist.Patch.Halt(__result = true);
				}
				if (!detour.puah.defHauls.TryGetValue(t.def, out storeCell))
				{
					return CodeOptimist.Patch.Halt(__result = true);
				}
				if (foundCell.GetSlotGroup(map) == storeCell.GetSlotGroup(map))
				{
					return CodeOptimist.Patch.Halt(__result = true);
				}
				newStoreCell = foundCell;
				if (!dumpIfStoreFilledAndAltsInopportune || IsNewStoreOpportune())
				{
					return CodeOptimist.Patch.Halt(__result = true);
				}
				if (DebugViewSettings.drawOpportunisticJobs)
				{
					for (int i = 0; i < 3; i++)
					{
						int duration = 600;
						map.debugDrawer.FlashCell(foundCell, 0.26f, carrier.Name.ToStringShort, duration);
						map.debugDrawer.FlashCell(storeCell, 0.22f, carrier.Name.ToStringShort, duration);
						map.debugDrawer.FlashCell(detour.opportunity.jobTarget.Cell, 0f, carrier.Name.ToStringShort, duration);
						map.debugDrawer.FlashLine(storeCell, foundCell, duration, SimpleColor.Yellow);
						map.debugDrawer.FlashLine(foundCell, detour.opportunity.jobTarget.Cell, duration, SimpleColor.Yellow);
						map.debugDrawer.FlashLine(storeCell, detour.opportunity.jobTarget.Cell, duration, SimpleColor.Green);
					}
					MoteMaker.ThrowText(storeCell.ToVector3(), carrier.Map, "Debug_CellOccupied".ModTranslate(), new Color(0.94f, 0.85f, 0f));
					MoteMaker.ThrowText(foundCell.ToVector3(), carrier.Map, "Debug_TooFar".ModTranslate(), Color.yellow);
					MoteMaker.ThrowText(carrier.DrawPos, carrier.Map, "Debug_Dropping".ModTranslate(), Color.green);
				}
				return CodeOptimist.Patch.Halt(__result = false);
			}
			BaseDetour baseDetour2 = detour;
			if ((object)baseDetour2 != null && baseDetour2.type == DetourType.PuahOpportunity && !detour.TrackPuahThingIfOpportune(t, carrier, ref foundCell))
			{
				return CodeOptimist.Patch.Halt(__result = false);
			}
			BaseDetour baseDetour3 = detour;
			if ((object)baseDetour3 != null && baseDetour3.type == DetourType.PuahBeforeCarry)
			{
				SlotGroup slotGroup = foundCell.GetSlotGroup(map);
				if (slotGroup != detour.beforeCarry.puah.storeCell.GetSlotGroup(map))
				{
					return CodeOptimist.Patch.Halt(__result = false);
				}
				if (slotGroup.Settings.Priority == t.Position.GetSlotGroup(map)?.Settings?.Priority || AmBleeding(carrier))
				{
					if (carrier.CurJobDef == JobDefOf.HaulToContainer && carrier.CurJob.targetC.Thing is Frame frame && frame.ThingCountNeeded(t.def) <= 0)
					{
						return CodeOptimist.Patch.Halt(__result = false);
					}
					if (carrier.CurJobDef == JobDefOf.DoBill && !carrier.CurJob.targetQueueB.Select((LocalTargetInfo x) => x.Thing?.def).Contains(t.def))
					{
						return CodeOptimist.Patch.Halt(__result = false);
					}
				}
			}
			if (puahToInventoryCallStack.Contains(PuahMethod_WorkGiver_HaulToInventory_AllocateThingAt))
			{
				detour = SetOrAddDetour(carrier, DetourType.ExistingElsePuah);
				detour.TrackPuahThing(t, foundCell);
			}
			return CodeOptimist.Patch.Halt(__result = true);
			bool IsNewStoreOpportune()
			{
				int num = storeCell.DistanceToSquared(newStoreCell);
				int num2 = storeCell.DistanceToSquared(detour.opportunity.jobTarget.Cell);
				if ((float)num > (float)num2 * settings.Opportunity_MaxNewLegsPctOrigTrip.Squared())
				{
					return false;
				}
				float num3 = storeCell.DistanceTo(newStoreCell);
				float num4 = newStoreCell.DistanceTo(detour.opportunity.jobTarget.Cell);
				float num5 = storeCell.DistanceTo(detour.opportunity.jobTarget.Cell);
				if (num3 + num4 > num5 * settings.Opportunity_MaxTotalTripPctOrigTrip)
				{
					return false;
				}
				return true;
			}
		}
	}

	private static readonly WeakDictionary<Pawn, BaseDetour> detours = new WeakDictionary<Pawn, BaseDetour>(8);

	private static readonly Type PuahType_CompHauledToInventory = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.CompHauledToInventory");

	private static readonly Type PuahType_WorkGiver_HaulToInventory = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.WorkGiver_HaulToInventory");

	private static readonly Type PuahType_JobDriver_HaulToInventory = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.JobDriver_HaulToInventory");

	private static readonly Type PuahType_JobDriver_UnloadYourHauledInventory = GenTypes.GetTypeInAnyAssembly("PickUpAndHaul.JobDriver_UnloadYourHauledInventory");

	private static readonly MethodInfo PuahMethod_WorkGiver_HaulToInventory_HasJobOnThing = AccessTools.DeclaredMethod(PuahType_WorkGiver_HaulToInventory, "HasJobOnThing");

	private static readonly MethodInfo PuahMethod_WorkGiver_HaulToInventory_JobOnThing = AccessTools.DeclaredMethod(PuahType_WorkGiver_HaulToInventory, "JobOnThing");

	private static readonly MethodInfo PuahMethod_WorkGiver_HaulToInventory_TryFindBestBetterStoreCellFor = AccessTools.DeclaredMethod(PuahType_WorkGiver_HaulToInventory, "TryFindBestBetterStoreCellFor");

	private static readonly MethodInfo PuahMethod_WorkGiver_HaulToInventory_AllocateThingAt = AccessTools.DeclaredMethod(PuahType_WorkGiver_HaulToInventory, "AllocateThingAtStoreTarget") ?? AccessTools.DeclaredMethod(PuahType_WorkGiver_HaulToInventory, "AllocateThingAtCell");

	private static readonly MethodInfo PuahMethod_JobDriver_UnloadYourHauledInventory_FirstUnloadableThing = AccessTools.DeclaredMethod(PuahType_JobDriver_UnloadYourHauledInventory, "FirstUnloadableThing");

	private static readonly MethodInfo PuahMethod_JobDriver_UnloadYourHauledInventory_MakeNewToils = AccessTools.DeclaredMethod(PuahType_JobDriver_UnloadYourHauledInventory, "MakeNewToils");

	private static readonly FieldInfo PuahField_WorkGiver_HaulToInventory_SkipCells = AccessTools.DeclaredField(PuahType_WorkGiver_HaulToInventory, "skipCells");

	private static readonly bool havePuah = new List<object>
	{
		PuahType_CompHauledToInventory, PuahType_WorkGiver_HaulToInventory, PuahType_JobDriver_HaulToInventory, PuahType_JobDriver_UnloadYourHauledInventory, PuahMethod_WorkGiver_HaulToInventory_HasJobOnThing, PuahMethod_WorkGiver_HaulToInventory_JobOnThing, PuahMethod_WorkGiver_HaulToInventory_TryFindBestBetterStoreCellFor, PuahMethod_WorkGiver_HaulToInventory_AllocateThingAt, PuahMethod_JobDriver_UnloadYourHauledInventory_FirstUnloadableThing, PuahMethod_JobDriver_UnloadYourHauledInventory_MakeNewToils,
		PuahField_WorkGiver_HaulToInventory_SkipCells
	}.All((object x) => x != null);

	private static readonly MethodInfo PuahMethod_CompHauledToInventory_GetComp = (havePuah ? AccessTools.DeclaredMethod(typeof(ThingWithComps), "GetComp").MakeGenericMethod(PuahType_CompHauledToInventory) : null);

	// Cached MethodInfo for CompHauledToInventory.GetHashSet — avoids Traverse
	// reflection on every call to DetourAwareFirstUnloadableThing.
	private static readonly MethodInfo PuahMethod_CompHauledToInventory_GetHashSet = (havePuah ? AccessTools.DeclaredMethod(PuahType_CompHauledToInventory, "GetHashSet") : null);

	private static readonly Type HugsType_Dialog_VanillaModSettings = GenTypes.GetTypeInAnyAssembly("HugsLib.Settings.Dialog_VanillaModSettings");

	private static readonly bool haveHugs = (object)HugsType_Dialog_VanillaModSettings != null;

	private static readonly FieldInfo SettingsCurModField = (haveHugs ? AccessTools.DeclaredField(HugsType_Dialog_VanillaModSettings, "selectedMod") : AccessTools.DeclaredField(typeof(Dialog_ModSettings), "mod"));

	private static readonly Type CsType_CommonSense = GenTypes.GetTypeInAnyAssembly("CommonSense.CommonSense");

	private static readonly Type CsType_Settings = GenTypes.GetTypeInAnyAssembly("CommonSense.Settings");

	private static readonly FieldInfo CsField_Settings_HaulingOverBills = AccessTools.DeclaredField(CsType_Settings, "hauling_over_bills");

	private static readonly bool haveCommonSense = new List<object> { CsType_CommonSense, CsType_Settings, CsField_Settings_HaulingOverBills }.All((object x) => x != null);

	private static Verse.Mod mod;

	private static Settings settings;

	private static bool foundConfig;

	private const string modId = "CodeOptimist.WhileYoureUp";

	private static readonly Harmony harmony = new Harmony("CodeOptimist.WhileYoureUp");

	private static readonly Dictionary<Thing, IntVec3> opportunityStoreCellCache = new Dictionary<Thing, IntVec3>(64);

	private static MaxRanges maxRanges;

	private static readonly List<Thing> haulables = new List<Thing>(32);

	private static readonly List<MethodBase> puahToInventoryCallStack = new List<MethodBase>(4);

	private static StorageSettings reducedPriorityStore;

	private static List<Thing> thingsInReducedPriorityStore;

	[TweakValue("WhileYoureUp.Unloading", 0f, 100f)]
	public static bool dumpIfStoreFilledAndAltsInopportune = true;

	private static readonly Dictionary<Thing, IntVec3> puahStoreCellCache = new Dictionary<Thing, IntVec3>(64);

	private static bool AlreadyHauling(Pawn pawn)
	{
		if (RealTime.frameCount == BaseDetour.lastFrameCount && pawn == BaseDetour.lastPawn)
		{
			return true;
		}
		if (detours.TryGetValue(pawn, out var value) && value.type != DetourType.Inactive)
		{
			return true;
		}
		if (havePuah)
		{
			HashSet<Thing> value2 = Traverse.Create((ThingComp)PuahMethod_CompHauledToInventory_GetComp.Invoke(pawn, null)).Field<HashSet<Thing>>("takenToInventory").Value;
			if (value2 != null && value2.Any((Thing t) => t != null))
			{
				return true;
			}
		}
		return false;
	}

	private static BaseDetour SetOrAddDetour(Pawn pawn, DetourType type, IntVec3? startCell = null, LocalTargetInfo? jobTarget = null, IntVec3? storeCell = null, LocalTargetInfo? carryTarget = null)
	{
		if (!detours.TryGetValue(pawn, out var value))
		{
			value = new BaseDetour();
			value.opportunity.puah.startCell = IntVec3.Invalid;
			value.opportunity.jobTarget = LocalTargetInfo.Invalid;
			value.beforeCarry.puah.storeCell = IntVec3.Invalid;
			value.beforeCarry.carryTarget = LocalTargetInfo.Invalid;
			detours[pawn] = value;
		}
		if (type == DetourType.ExistingElsePuah)
		{
			DetourType type2 = value.type;
			// Early return if already in a Puah-family state — preserves
			// the defHauls cache across ticks instead of clearing and
			// rebuilding it every call. Original code only checked types
			// 5-6 (PuahOpportunity/PuahBeforeCarry) but missed type Puah
			// itself, causing a full storage cell search every tick.
			if ((uint)(type2 - 5) <= 1u || type2 == DetourType.Puah)
			{
				return Result(value);
			}
			type = DetourType.Puah;
		}
		value.opportunity.puah.startCell = startCell ?? IntVec3.Invalid;
		value.opportunity.jobTarget = jobTarget ?? LocalTargetInfo.Invalid;
		value.beforeCarry.puah.storeCell = storeCell ?? IntVec3.Invalid;
		value.beforeCarry.carryTarget = carryTarget ?? LocalTargetInfo.Invalid;
		value.Deactivate();
		value.type = type;
		return Result(value);
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static BaseDetour Result(BaseDetour result)
		{
			return result;
		}
	}

	private static Job PuahJob(Pawn pawn, Thing thing)
	{
		WorkGiver worker = DefDatabase<WorkGiverDef>.GetNamed("HaulToInventory").Worker;
		return (Job)PuahMethod_WorkGiver_HaulToInventory_JobOnThing.Invoke(worker, new object[3] { pawn, thing, false });
	}

	private static Job BeforeCarryDetour_Job(Pawn pawn, LocalTargetInfo carryTarget, Thing thing)
	{
		if (thing.ParentHolder is Pawn_InventoryTracker)
		{
			return null;
		}
		if (MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, 2))
		{
			return null;
		}
		if (!TryFindBestBetterStoreCellFor_MidwayToTarget(thing, LocalTargetInfo.Invalid, carryTarget, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out var foundCell, needAccurateResult: true))
		{
			return null;
		}
		int num = thing.Position.DistanceToSquared(carryTarget.Cell);
		if (foundCell.DistanceToSquared(carryTarget.Cell) < num)
		{
			if (DebugViewSettings.drawOpportunisticJobs)
			{
				for (int i = 0; i < 3; i++)
				{
					int duration = 600;
					pawn.Map.debugDrawer.FlashCell(thing.Position, 0.62f, pawn.Name.ToStringShort, duration);
					pawn.Map.debugDrawer.FlashCell(foundCell, 0.22f, pawn.Name.ToStringShort, duration);
					pawn.Map.debugDrawer.FlashCell(carryTarget.Cell, 0f, pawn.Name.ToStringShort, duration);
					pawn.Map.debugDrawer.FlashLine(thing.Position, carryTarget.Cell, duration, SimpleColor.Magenta);
					pawn.Map.debugDrawer.FlashLine(thing.Position, foundCell, duration, SimpleColor.Cyan);
					pawn.Map.debugDrawer.FlashLine(foundCell, carryTarget.Cell, duration, SimpleColor.Cyan);
				}
			}
			LocalTargetInfo? carryTarget2;
			if (havePuah && settings.UsePickUpAndHaulPlus)
			{
				IntVec3? storeCell = foundCell;
				carryTarget2 = carryTarget;
				SetOrAddDetour(pawn, DetourType.PuahBeforeCarry, null, null, storeCell, carryTarget2).TrackPuahThing(thing, foundCell);
				Job job = PuahJob(pawn, thing);
				if (job != null)
				{
					return job;
				}
			}
			carryTarget2 = carryTarget;
			SetOrAddDetour(pawn, DetourType.HtcBeforeCarry, null, null, null, carryTarget2);
			return HaulAIUtility.HaulToCellStorageJob(pawn, thing, foundCell, fitInStoreCell: false);
		}
		return null;
	}

	public Mod(ModContentPack content)
		: base(content)
	{
		mod = this;
		Gui.keyPrefix = "CodeOptimist.WhileYoureUp";
		settings = GetSettings<Settings>();
		if (!foundConfig)
		{
			settings.ExposeData();
		}
		harmony.PatchAll();
	}

	public override string SettingsCategory()
	{
		return mod.Content.Name;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool AmBleeding(Pawn pawn)
	{
		return pawn.health.hediffSet.BleedRateTotal > 0.001f;
	}

	private static Job Opportunity_Job(Pawn pawn, LocalTargetInfo jobTarget)
	{
		Job result = _Opportunity_Job();
		opportunityStoreCellCache.Clear();
		return result;
		Job _Opportunity_Job()
		{
			maxRanges.Reset();
			haulables.Clear();
			haulables.AddRange(pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling());
			int num = 0;
			while (haulables.Count > 0)
			{
				if (num == haulables.Count)
				{
					maxRanges *= MaxRanges.heuristicRangeExpandFactor;
					num = 0;
				}
				Thing thing = haulables[num];
				IntVec3 storeCell;
				switch (CanHaul(pawn, thing, jobTarget, out storeCell))
				{
					case CanHaulResult.RangeFail:
						if (settings.Opportunity_PathChecker != Settings.PathCheckerEnum.Vanilla)
						{
							num++;
							break;
						}
						goto case CanHaulResult.HardFail;
					case CanHaulResult.HardFail:
						haulables.RemoveAt(num);
						break;
					case CanHaulResult.FullStop:
						return null;
					case CanHaulResult.Success:
						{
							if (DebugViewSettings.drawOpportunisticJobs)
							{
								for (int i = 0; i < 3; i++)
								{
									int duration = 600;
									pawn.Map.debugDrawer.FlashCell(pawn.Position, 0.5f, pawn.Name.ToStringShort, duration);
									pawn.Map.debugDrawer.FlashCell(thing.Position, 0.62f, pawn.Name.ToStringShort, duration);
									pawn.Map.debugDrawer.FlashCell(storeCell, 0.22f, pawn.Name.ToStringShort, duration);
									pawn.Map.debugDrawer.FlashCell(jobTarget.Cell, 0f, pawn.Name.ToStringShort, duration);
									pawn.Map.debugDrawer.FlashLine(pawn.Position, jobTarget.Cell, duration, SimpleColor.Red);
									pawn.Map.debugDrawer.FlashLine(pawn.Position, thing.Position, duration, SimpleColor.Green);
									pawn.Map.debugDrawer.FlashLine(thing.Position, storeCell, duration, SimpleColor.Green);
									pawn.Map.debugDrawer.FlashLine(storeCell, jobTarget.Cell, duration, SimpleColor.Green);
								}
							}
							if (havePuah && settings.UsePickUpAndHaulPlus)
							{
								SetOrAddDetour(pawn, DetourType.PuahOpportunity, pawn.Position, jobTarget).TrackPuahThing(thing, storeCell);
								Job job = PuahJob(pawn, thing);
								if (job != null)
								{
									return job;
								}
							}
							Pawn pawn2 = pawn;
							LocalTargetInfo? jobTarget2 = jobTarget;
							SetOrAddDetour(pawn2, DetourType.HtcOpportunity, null, jobTarget2);
							return HaulAIUtility.HaulToCellStorageJob(pawn, thing, storeCell, fitInStoreCell: false);
						}
				}
			}
			return null;
		}
	}

	private static CanHaulResult CanHaul(Pawn pawn, Thing thing, LocalTargetInfo jobTarget, out IntVec3 storeCell)
	{
		storeCell = IntVec3.Invalid;
		int num = pawn.Position.DistanceToSquared(thing.Position);
		if ((float)num > maxRanges.startToThing.Squared())
		{
			return CanHaulResult.RangeFail;
		}
		int num2 = pawn.Position.DistanceToSquared(jobTarget.Cell);
		if ((float)num > (float)num2 * maxRanges.startToThingPctOrigTrip.Squared())
		{
			return CanHaulResult.RangeFail;
		}
		float num3 = pawn.Position.DistanceTo(thing.Position);
		float num4 = thing.Position.DistanceTo(jobTarget.Cell);
		float num5 = pawn.Position.DistanceTo(jobTarget.Cell);
		if (num3 + num4 > num5 * settings.Opportunity_MaxTotalTripPctOrigTrip)
		{
			return CanHaulResult.HardFail;
		}
		if (pawn.Map.reservationManager.FirstRespectedReserver(thing, pawn) != null)
		{
			return CanHaulResult.HardFail;
		}
		if (thing.IsForbidden(pawn))
		{
			return CanHaulResult.HardFail;
		}
		if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced: false))
		{
			return CanHaulResult.HardFail;
		}
		StoragePriority currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
		if (!opportunityStoreCellCache.TryGetValue(thing, out storeCell) && !TryFindBestBetterStoreCellFor_MidwayToTarget(thing, jobTarget, IntVec3.Invalid, pawn, pawn.Map, currentPriority, pawn.Faction, out storeCell, maxRanges.expandCount == 0))
		{
			return CanHaulResult.HardFail;
		}
		opportunityStoreCellCache.SetOrAdd(thing, storeCell);
		int num6 = storeCell.DistanceToSquared(jobTarget.Cell);
		if ((float)num6 > maxRanges.storeToJob.Squared())
		{
			return CanHaulResult.RangeFail;
		}
		if ((float)num6 > (float)num2 * maxRanges.storeToJobPctOrigTrip.Squared())
		{
			return CanHaulResult.RangeFail;
		}
		float num7 = storeCell.DistanceTo(jobTarget.Cell);
		if (num3 + num7 > num5 * settings.Opportunity_MaxNewLegsPctOrigTrip)
		{
			return CanHaulResult.HardFail;
		}
		float num8 = thing.Position.DistanceTo(storeCell);
		if (num3 + num8 + num7 > num5 * settings.Opportunity_MaxTotalTripPctOrigTrip)
		{
			return CanHaulResult.HardFail;
		}
		return settings.Opportunity_PathChecker switch
		{
			Settings.PathCheckerEnum.Vanilla => (!WithinRegionCount(storeCell)) ? CanHaulResult.HardFail : CanHaulResult.Success,
			Settings.PathCheckerEnum.Pathfinding => (!WithinPathCost(storeCell)) ? CanHaulResult.HardFail : CanHaulResult.Success,
			Settings.PathCheckerEnum.Default => WithinPathCost(storeCell) ? CanHaulResult.Success : CanHaulResult.FullStop,
			_ => throw new ArgumentOutOfRangeException(),
		};
		float GetPathCost(IntVec3 start, LocalTargetInfo destTarget, PathEndMode peMode)
		{
			PawnPath pawnPath = pawn.Map.pathFinder.FindPathNow(
				start,
				(LocalTargetInfo)destTarget.Cell,
				TraverseParms.For(pawn, Danger.Deadly, TraverseMode.ByPawn, false, false, false),
				(PathFinderCostTuning?)null,
				peMode
			);
			float totalCost = pawnPath.TotalCost;
			pawnPath.ReleaseToPool();
			return totalCost;
		}
		bool WithinPathCost(IntVec3 intVec)
		{
			float num9 = GetPathCost(pawn.Position, thing, PathEndMode.ClosestTouch);
			if (num9 == 0f)
			{
				return false;
			}
			float num10 = GetPathCost(intVec, jobTarget, PathEndMode.Touch);
			if (num10 == 0f)
			{
				return false;
			}
			float num11 = GetPathCost(pawn.Position, jobTarget, PathEndMode.Touch);
			if (num11 == 0f)
			{
				return false;
			}
			if (num9 + num10 > num11 * settings.Opportunity_MaxNewLegsPctOrigTrip)
			{
				return false;
			}
			float num12 = GetPathCost(thing.Position, intVec, PathEndMode.ClosestTouch);
			if (num12 == 0f)
			{
				return false;
			}
			if (num9 + num12 + num10 > num11 * settings.Opportunity_MaxTotalTripPctOrigTrip)
			{
				return false;
			}
			return true;
		}
		bool WithinRegionCount(IntVec3 a)
		{
			if (!pawn.Position.WithinRegions(thing.Position, pawn.Map, settings.Opportunity_MaxStartToThingRegionLookCount, TraverseParms.For(pawn, Danger.Deadly, TraverseMode.ByPawn, false, false, false)))
			{
				return false;
			}
			if (!a.WithinRegions(jobTarget.Cell, pawn.Map, settings.Opportunity_MaxStoreToJobRegionLookCount, TraverseParms.For(pawn, Danger.Deadly, TraverseMode.ByPawn, false, false, false)))
			{
				return false;
			}
			return true;
		}
	}

	private static void PushHtiMethod(MethodBase method)
	{
		puahToInventoryCallStack.Add(method);
	}

	private static void PopHtiMethod()
	{
		if (puahToInventoryCallStack.Any())
		{
			puahToInventoryCallStack.Pop();
			if (!puahToInventoryCallStack.Any())
			{
				puahStoreCellCache.Clear();
			}
		}
	}

	public override void DoSettingsWindowContents(Rect inRect)
	{
		SettingsWindow.DoWindowContents(inRect);
	}

	private static bool TryFindBestBetterStoreCellFor_MidwayToTarget(Thing thing, LocalTargetInfo opportunityTarget, LocalTargetInfo beforeCarryTarget, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, bool needAccurateResult, HashSet<IntVec3> skipCells = null)
	{
		IntVec3 intVec = IntVec3.Invalid;
		float num = 2.1474836E+09f;
		StoragePriority storagePriority = currentPriority;
		foreach (SlotGroup item in map.haulDestinationManager.AllGroupsListInPriorityOrder)
		{
			if ((int)item.Settings.Priority < (int)storagePriority || (int)item.Settings.Priority < (int)currentPriority || item.Settings.Priority == StoragePriority.Unstored || (item.Settings.Priority == currentPriority && !beforeCarryTarget.IsValid))
			{
				break;
			}
			Zone_Stockpile zone_Stockpile = item.parent as Zone_Stockpile;
			Building_Storage building_Storage = item.parent as Building_Storage;
			if (opportunityTarget.IsValid && ((zone_Stockpile != null && !settings.Opportunity_ToStockpiles) || (building_Storage != null && !settings.Opportunity_BuildingFilter.Allows(building_Storage.def))))
			{
				continue;
			}
			if (beforeCarryTarget.IsValid)
			{
				if (!settings.HaulBeforeCarry_ToEqualPriority && item.Settings.Priority == currentPriority)
				{
					break;
				}
				if ((settings.HaulBeforeCarry_ToEqualPriority && thing.Position.IsValid && item == map.haulDestinationManager.SlotGroupAt(thing.Position)) || (zone_Stockpile != null && !settings.HaulBeforeCarry_ToStockpiles) || (building_Storage != null && (!settings.HaulBeforeCarry_BuildingFilter.Allows(building_Storage.def) || (item.Settings.Priority == currentPriority && !settings.Opportunity_BuildingFilter.Allows(building_Storage.def)))))
				{
					continue;
				}
			}
			if (!item.parent.Accepts(thing))
			{
				continue;
			}
			IntVec3 intVec2 = (thing.SpawnedOrAnyParentSpawned ? thing.PositionHeld : carrier.PositionHeld);
			IntVec3 intVec3 = (opportunityTarget.IsValid ? opportunityTarget.Cell : (beforeCarryTarget.IsValid ? beforeCarryTarget.Cell : IntVec3.Invalid));
			IntVec3 intVec4 = (intVec3.IsValid ? new IntVec3((intVec3.x + intVec2.x) / 2, intVec3.y, (intVec3.z + intVec2.z) / 2) : IntVec3.Invalid);
			IntVec3 intVec5 = (intVec4.IsValid ? intVec4 : intVec2);
			int num2 = (needAccurateResult ? ((int)Math.Floor((double)item.CellsList.Count * (double)Rand.Range(0.005f, 0.018f))) : 0);
			for (int i = 0; i < item.CellsList.Count; i++)
			{
				IntVec3 intVec6 = item.CellsList[i];
				float num3 = (intVec5 - intVec6).LengthHorizontalSquared;
				if (!(num3 > num) && (skipCells == null || !skipCells.Contains(intVec6)) && StoreUtility.IsGoodStoreCell(intVec6, map, thing, carrier, faction))
				{
					intVec = intVec6;
					num = num3;
					storagePriority = item.Settings.Priority;
					if (i >= num2)
					{
						break;
					}
				}
			}
		}
		foundCell = intVec;
		if (foundCell.IsValid)
		{
			skipCells?.Add(foundCell);
		}
		return foundCell.IsValid;
	}

	// [DebugAction("Autotests", "Make colony (While You're Up)", false, false, false, false, 0, false, allowedGameStates = AllowedGameStates.PlayingOnMap)]
	// private static void MakeColonyWyu()
	// {
	// 	bool godMode = DebugSettings.godMode;
	// 	DebugSettings.godMode = true;
	// 	DebugViewSettings.drawOpportunisticJobs = true;
	// 	Thing.allowDestroyNonDestroyable = true;
	// 	if (Autotests_ColonyMaker.usedCells == null)
	// 	{
	// 		Autotests_ColonyMaker.usedCells = new BoolGrid(Autotests_ColonyMaker.Map);
	// 	}
	// 	else
	// 	{
	// 		Autotests_ColonyMaker.usedCells.ClearAndResizeTo(Autotests_ColonyMaker.Map);
	// 	}
	// 	Autotests_ColonyMaker.overRect = new CellRect(Autotests_ColonyMaker.Map.Center.x - 50, Autotests_ColonyMaker.Map.Center.z - 50, 100, 50);
	// 	Autotests_ColonyMaker.DeleteAllSpawnedPawns();
	// 	GenDebug.ClearArea(Autotests_ColonyMaker.overRect, Find.CurrentMap);
	// 	Autotests_ColonyMaker.Map.wealthWatcher.ForceRecount();
	// 	Autotests_ColonyMaker.TryGetFreeRect(90, 30, out CellRect result);
	// 	foreach (ThingDef item in DefDatabase<ThingDef>.AllDefs.Where((ThingDef def) => typeof(Building_WorkTable).IsAssignableFrom(def.thingClass)))
	// 	{
	// 		if (!(Autotests_ColonyMaker.TryMakeBuilding(item) is Building_WorkTable building_WorkTable))
	// 		{
	// 			continue;
	// 		}
	// 		foreach (RecipeDef allRecipe in building_WorkTable.def.AllRecipes)
	// 		{
	// 			building_WorkTable.billStack.AddBill(allRecipe.MakeNewBill());
	// 		}
	// 	}
	// 	result = result.ContractedBy(1);
	// 	foreach (ThingDef item2 in DefDatabase<ThingDef>.AllDefs.Where((ThingDef def) => DebugThingPlaceHelper.IsDebugSpawnable(def) && def.category == ThingCategory.Item).ToList())
	// 	{
	// 		DebugThingPlaceHelper.DebugSpawn(item2, result.RandomCell, -1, direct: true);
	// 	}
	// 	int num = 30;
	// 	List<TimeAssignmentDef> times = Enumerable.Repeat(TimeAssignmentDefOf.Work, 24).ToList();
	// 	for (int num2 = 0; num2 < num; num2++)
	// 	{
	// 		Pawn pawn = PawnGenerator.GeneratePawn(Faction.OfPlayer.def.basicMemberKind, Faction.OfPlayer);
	// 		pawn.needs.mood.thoughts.memories.TryGainMemory(ThoughtDefOf.NewColonyOptimism);
	// 		pawn.needs.mood.thoughts.memories.TryGainMemory(ThoughtDefOf.NewColonyHope);
	// 		pawn.timetable.times = times;
	// 		foreach (WorkTypeDef allDef in DefDatabase<WorkTypeDef>.AllDefs)
	// 		{
	// 			if (!pawn.WorkTypeIsDisabled(allDef))
	// 			{
	// 				pawn.workSettings.SetPriority(allDef, 3);
	// 			}
	// 		}
	// 		pawn.workSettings.Disable(WorkTypeDefOf.Hauling);
	// 		GenSpawn.Spawn(pawn, result.RandomCell, Autotests_ColonyMaker.Map);
	// 	}
	// 	Designator_ZoneAddStockpile_Resources designator_ZoneAddStockpile_Resources = new Designator_ZoneAddStockpile_Resources();
	// 	for (int num3 = 0; num3 < 7; num3++)
	// 	{
	// 		Autotests_ColonyMaker.TryGetFreeRect(8, 8, out CellRect result2);
	// 		result2 = result2.ContractedBy(1);
	// 		designator_ZoneAddStockpile_Resources.DesignateMultiCell(result2.Cells);
	// 		((Zone_Stockpile)Autotests_ColonyMaker.Map.zoneManager.ZoneAt(result2.CenterCell)).settings.Priority = StoragePriority.Normal;
	// 	}
	// 	Autotests_ColonyMaker.ClearAllHomeArea();
	// 	Autotests_ColonyMaker.FillWithHomeArea(Autotests_ColonyMaker.overRect);
	// 	DebugSettings.godMode = godMode;
	// 	Thing.allowDestroyNonDestroyable = false;
	// }
}
