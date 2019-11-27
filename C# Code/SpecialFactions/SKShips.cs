using Arcen.AIW2.Core;
using Arcen.AIW2.External;
using Arcen.Universal;
using System;
using System.Collections.Generic;

namespace Arcen.AIW2.SK
{
    // Loaded by each individual Virion.
    public class VirionSquadData
    {
        public int Energy;
        public int LastHitSecond; // Last second that we've processed a hit from this ship.

        // Following three functions are used for initializing, saving, and loading data.
        // Initialization function.
        // Default values. Called on creation, NOT on load.
        public VirionSquadData()
        {
            Energy = 0;
            LastHitSecond = 0;
        }
        // Saving our data.
        public void SerializeTo( ArcenSerializationBuffer Buffer )
        {
            Buffer.AddItem( this.Energy );
            Buffer.AddItem( this.LastHitSecond );
        }
        // Loading our data. Make sure the loading order is the same as the saving order.
        public VirionSquadData( ArcenDeserializationBuffer Buffer )
        {
            this.Energy = Buffer.ReadInt32();
            this.LastHitSecond = Buffer.ReadInt32();
        }
    }

    // Description appender for Virion. Used to display energy on mouseover.
    public class VirionDescriptionAppender : IGameEntityDescriptionAppender
    {
        public void AddToDescriptionBuffer( GameEntity_Squad RelatedEntityOrNull, GameEntityTypeData RelatedEntityTypeData, ArcenDoubleCharacterBuffer Buffer )
        {
            if ( RelatedEntityOrNull == null )
                return;

            // Skip if final virion.
            if ( RelatedEntityTypeData.InternalName == "SKVirion5" )
                return;

            VirionSquadData virionSquadData = RelatedEntityOrNull.GetVirionSquadDataExt();
            if ( virionSquadData == null )
                return;

            // Figure out how much energy to get to our next tier.
            int requiredEnergy;
            string currentTypeName = RelatedEntityOrNull.TypeData.InternalName;
            int nextTierNumber;
            if ( !int.TryParse( currentTypeName.Substring( currentTypeName.Length - 1, 1 ), out nextTierNumber ) )
                return;
            nextTierNumber++;
            string nextTierName = "SKVirion" + nextTierNumber;
            GameEntityTypeData nextTier;
            try
            {
                nextTier = GameEntityTypeDataTable.Instance.GetRowByName( nextTierName, false, null );
                int count = RelatedEntityOrNull.FleetMembership.Fleet.GetOrAddMembershipGroupBasedOnSquadType_AssumeNoDuplicates( nextTier ).EffectiveSquadCap;
                requiredEnergy = (int)Math.Pow( 10, nextTierNumber ) * Math.Max( count, 1 );
            }
            catch ( System.Exception e )
            {
                ArcenDebugging.SingleLineQuickDebug( "SKShips - Virion Fail #3" + e.ToString() );
                return;
            }

            Buffer.Add( " Meld Energy: " + virionSquadData.Energy + "/" + requiredEnergy + " " );
        }
    }

    // The main faction class.
    // We'll basically be using a minor faction for all of our ships logic.
    // This requires this faction to be enabled in the lobby in order to seed things, acting like a global toggle for the ships.
    // This allows it to remain enabled even if not used, without modifying the game.
    // (Its also a really easy way to do things in general. A faction doesn't actually need to 'act' like a faction.)
    public class SpecialFaction_SKShips : BaseSpecialFaction
    {
        // List of PrimaryKeyIDs for all ships added by the mod.
        protected List<int> shipKeys;

        // Store any centerpieces for active SKVirion ships here.
        protected List<int> virionCenterpieces = new List<int>();

        // Information required for our faction.
        // General identifier for our faction.
        protected override string TracingName => "SKShips";

        // Let the game know we're going to want to use the DoLongRangePlanning_OnBackgroundNonSimThread_Subclass function.
        // This function is generally used for things that do not need to always run, such as navigation requests.
        protected override bool EverNeedsToRunLongRangePlanning => true;

        // How often our DoLongRangePlanning_OnBackgroundNonSimThread_Subclass function is called in seconds.
        // In our case, we'll be doing it once per 5 seconds.
        protected override FInt MinimumSecondsBetweenLongRangePlannings => FInt.FromParts( 5, 000 );

        // The following function is called once every second.
        // Generally a good idea to do most things here, as its consistant regardless of game speed.
        public override void DoPerSecondLogic_Stage3Main_OnMainThread( Faction faction, ArcenSimContext Context )
        {
            allyThisFactionToHumans( faction );

            // Update our ship list.
            if ( shipKeys != null )
            {
                // Attempt to populate the list.
                World_AIW2.Instance.DoForEntities( delegate ( GameEntity_Squad entity )
                {
                    if ( entity.TypeData.GetHasTag( "SKShips" ) && !shipKeys.Contains( entity.PrimaryKeyID ) )
                        shipKeys.Add( entity.PrimaryKeyID );
                    return DelReturn.Continue;
                } );
            }
            for ( int x = 0; x < shipKeys.Count; x++ )
            {
                GameEntity_Squad ship = World_AIW2.Instance.GetEntityByID_Squad( shipKeys[x] );
                if ( ship == null )
                {
                    // Ship not found. Remove from our list.
                    shipKeys.RemoveAt( x );
                    x--;
                    continue;
                }
                // Formica ship, locks nearby ships in a health draining, zombifiying tractor beam.
                if ( ship.TypeData.InternalName == "SKFormica" )
                {
                    // For each tractored source, drain some health and output a weak drone copy.
                    for ( int k = 0; k < ship.CurrentlyStrongestTractorSourceHittingThese.Count; k++ )
                    {
                        int id = ship.CurrentlyStrongestTractorSourceHittingThese[k];
                        GameEntity_Squad tractoredEntity = World_AIW2.Instance.GetEntityByID_Squad( id );
                        if ( tractoredEntity == null )
                            continue;
                        int damage = (int)(tractoredEntity.DataForMark.HullPoints * (0.01 * ship.CurrentMarkLevel));
                        tractoredEntity.TakeDamage( damage, null, null, false, Context );
                        ship.TakeHullRepair( damage );
                        int zombieCount = 0;
                        int formicaCount = 0;
                        Faction destinationFaction = World_AIW2.Instance.GetFirstFactionWithSpecialFactionImplementationType( typeof( SpecialFaction_AntiAIZombie ) );
                        ship.Planet.DoForEntities( delegate ( GameEntity_Squad squad )
                        {
                            if ( squad.PlanetFaction.Faction.FactionIndex == destinationFaction.FactionIndex
                            && squad.TypeData.InternalName == tractoredEntity.TypeData.InternalName )
                                zombieCount += 1 + squad.ExtraStackedSquadsInThis;
                            else if ( squad.PlanetFaction.Faction.FactionIndex == ship.PlanetFaction.Faction.FactionIndex
                            && squad.TypeData.InternalName == ship.TypeData.InternalName )
                                formicaCount += 1 + squad.ExtraStackedSquadsInThis + squad.CurrentMarkLevel;
                            return DelReturn.Continue;
                        } );
                        if ( zombieCount < formicaCount )
                        {
                            // Yoinked from zombification code.
                            if ( destinationFaction == null )
                                return;
                            PlanetFaction factionForNewEntity = tractoredEntity.Planet.GetPlanetFactionForFaction( destinationFaction );
                            GameEntity_Squad zombie = GameEntity_Squad.CreateNew( factionForNewEntity, tractoredEntity.TypeData, tractoredEntity.CurrentMarkLevel,
                                factionForNewEntity.Faction.LooseFleet, 0, tractoredEntity.WorldLocation, Context );
                            zombie.TakeHullRepair( (int)(-zombie.DataForMark.HullPoints * 0.75) );
                            zombie.ShouldNotBeConsideredAsThreatToHumanTeam = true;
                            zombie.Orders.SetBehavior( EntityBehaviorType.Attacker_Full, -1 );
                        }
                        // Already at max zombo, do a second burst of damage instead.
                        else
                        {
                            tractoredEntity.TakeDamage( damage, null, null, false, Context );
                            ship.TakeHullRepair( damage );
                        }
                    }
                }
                // Virion, gains energy as it deals damage. (Just adding centerpieces for their fleets here to later process each virion individually.)
                else if ( ship.TypeData.InternalName.Contains( "SKVirion" ) && ship.TypeData.InternalName != "SKVirion5" )
                {
                    // Get energy from the latest hit, if not already processed.
                    VirionSquadData virionSquadData = ship.GetVirionSquadDataExt();
                    if ( ship.Systems[0].LastGameSecondMyShotHit != virionSquadData.LastHitSecond )
                    {
                        virionSquadData.LastHitSecond = ship.Systems[0].LastGameSecondMyShotHit;
                        virionSquadData.Energy += ship.Systems[0].LastTotalDamageMyShotDidCaused;

                        ship.SetVirionSquadDataExt( virionSquadData );
                    }
                    // Add virion ship's centerpiece to be processed below.
                    if ( !virionCenterpieces.Contains( ship.FleetMembership.Fleet.Centerpiece.GetSquad().PrimaryKeyID ) )
                        virionCenterpieces.Add( ship.FleetMembership.Fleet.Centerpiece.GetSquad().PrimaryKeyID );
                }
                // Hydran Surgeons, heals non-Surgeon units in its fleet whenever it deals damage.
                else if (ship.TypeData.GetHasTag( "SKSurgeon" ) )
                {
                    if ( ship.Systems[0].LastGameSecondMyShotHit == World_AIW2.Instance.GameSecond - 1
                        && ship.Systems[0].LastTotalDamageMyShotDidCaused > 0)
                    {
                        ship.FleetMembership.Fleet.DoForMemberGroups( delegate ( Fleet.Membership membership )
                         {
                             if ( membership.TypeData.GetHasTag( "SKSurgeon" ) )
                                 return DelReturn.Continue;

                             for(int y = 0; y < membership.Entities.Count; y++ )
                             {
                                 GameEntity_Squad memSquad = membership.Entities[y].GetSquad();
                                 if ( memSquad == null )
                                     continue;

                                 memSquad.TakeHullRepair( ship.Systems[0].LastTotalDamageMyShotDidCaused / 10 );
                             }

                             return DelReturn.Continue;
                         } );
                    }
                }
            }
            // Virion fleet logic.
            // Find any valid meld-ready ships, combine them, and update caps.
            for ( int x = 0; x < virionCenterpieces.Count; x++ )
            {
                GameEntity_Squad centerpiece = World_AIW2.Instance.GetEntityByID_Squad( virionCenterpieces[x] );
                Fleet virionFleet = centerpiece.FleetMembership.Fleet;
                //virionFleet.RemoveAnEmptySlotIfOneExists(); // Only allow Virion ships onto a Virion fleet.
                // Get fleet requirements, and any meld ready ships for each tier.
                Dictionary<string, List<GameEntity_Squad>> meldReadyShips = new Dictionary<string, List<GameEntity_Squad>>();
                virionFleet.DoForEntities( delegate ( GameEntity_Squad membershipSquad )
                {
                    // Only process our virion ships.
                    if ( !membershipSquad.TypeData.InternalName.Contains( "SKVirion" )
                    || membershipSquad.TypeData.InternalName == "SKVirion5" )
                        return DelReturn.Continue;

                    // Figure out how much energy to get to our next tier.
                    int requiredEnergy;
                    string currentTypeName = membershipSquad.TypeData.InternalName;
                    int nextTierNumber;
                    if ( !int.TryParse( currentTypeName.Substring( currentTypeName.Length - 1, 1 ), out nextTierNumber ) )
                        return DelReturn.Continue;
                    nextTierNumber++;
                    string nextTierName = "SKVirion" + nextTierNumber;
                    GameEntityTypeData nextTier;
                    try
                    {
                        nextTier = GameEntityTypeDataTable.Instance.GetRowByName( nextTierName, false, null );
                        int count = virionFleet.GetOrAddMembershipGroupBasedOnSquadType_AssumeNoDuplicates( nextTier ).EffectiveSquadCap;
                        requiredEnergy = (int)Math.Pow( 10, nextTierNumber ) * Math.Max( count, 1 );
                    }
                    catch ( System.Exception e )
                    {
                        ArcenDebugging.SingleLineQuickDebug( "SKShips - Virion Fail Line #1" + e.ToString() );
                        return DelReturn.Continue;
                    }

                    // If we've enough energy for our next tier, ready up.
                    if ( membershipSquad.GetVirionSquadDataExt().Energy > requiredEnergy )
                    {
                        if ( !meldReadyShips.ContainsKey( currentTypeName ) )
                        {
                            meldReadyShips.Add( currentTypeName, new List<GameEntity_Squad>() );
                            meldReadyShips[currentTypeName].Add( membershipSquad );
                        }
                        else
                            meldReadyShips[currentTypeName].Add( membershipSquad );
                        VirionSquadData virionSquadData = membershipSquad.GetVirionSquadDataExt();
                        membershipSquad.SetVirionSquadDataExt( virionSquadData );
                    }

                    return DelReturn.Continue;
                } );
                foreach ( string key in meldReadyShips.Keys )
                {
                    if ( meldReadyShips[key].Count > 1 )
                    {
                        int nextTierNumber;
                        if ( !int.TryParse( key.Substring( key.Length - 1, 1 ), out nextTierNumber ) )
                            continue;
                        nextTierNumber++;
                        string nextTierName = "SKVirion" + nextTierNumber;
                        GameEntityTypeData nextTier;
                        try
                        {
                            nextTier = GameEntityTypeDataTable.Instance.GetRowByName( nextTierName, false, null );
                            int count = virionFleet.GetOrAddMembershipGroupBasedOnSquadType_AssumeNoDuplicates( nextTier ).EffectiveSquadCap;
                        }
                        catch ( System.Exception e )
                        {
                            ArcenDebugging.SingleLineQuickDebug( "SKShips - Virion Fail #2" + e.ToString() );
                            continue;
                        }

                        GameEntityTypeData currentTier = GameEntityTypeDataTable.Instance.GetRowByName( key, false, null );
                        ArcenPoint spawnLocation = meldReadyShips[key][0].WorldLocation;
                        GameEntity_Squad nextSquad = GameEntity_Squad.CreateNew( centerpiece.PlanetFaction, nextTier,
                            meldReadyShips[key][0].CurrentMarkLevel, virionFleet, 0, spawnLocation, Context );
                        nextSquad.Orders.SetBehavior( meldReadyShips[key][0].Orders.Behavior, meldReadyShips[key][0].PlanetFaction.Faction.FactionIndex );

                        virionFleet.GetOrAddMembershipGroupBasedOnSquadType_AssumeNoDuplicates( currentTier ).ExplicitBaseSquadCap -= 2;

                        virionFleet.AddSquadToMembership_AssumeNoDuplicates( nextSquad );

                        virionFleet.GetOrAddMembershipGroupBasedOnSquadType_AssumeNoDuplicates( nextTier ).ExplicitBaseSquadCap = Math.Max(
                            1, virionFleet.GetOrAddMembershipGroupBasedOnSquadType_AssumeNoDuplicates( nextTier ).ExplicitBaseSquadCap + 1 );

                        meldReadyShips[key][0].Despawn( Context, true, InstancedRendererDeactivationReason.TransformedIntoAnotherEntityType );
                        meldReadyShips[key][1].Despawn( Context, true, InstancedRendererDeactivationReason.TransformedIntoAnotherEntityType );
                    }
                }
            }
        }

        // Called multiple times per second, use carefully.
        // Game speed modifiers can directly reduce the accuracy of things done here.
        public override void DoPerSimStepLogic_OnMainThread( Faction faction, ArcenSimContext Context )
        {
            // If we haven't loaded our ship list yet, due to loading a save, or starting a new game,
            // we want to immediately find all ships added by the mod.
            if ( shipKeys == null )
                // Create an empty list.
                shipKeys = new List<int>();
            for ( int x = 0; x < shipKeys.Count; x++ )
            {
                GameEntity_Squad ship = World_AIW2.Instance.GetEntityByID_Squad( shipKeys[x] );
                if ( ship == null )
                {
                    // Ship not found. Remove from our list.
                    shipKeys.RemoveAt( x );
                    x--;
                    continue;
                }
                // Time for custom logic.
                // Whirpool ship, knock nearby hostile mobile ships away. Slowly.
                if ( ship.TypeData.InternalName == "SKWhirlpool" )
                {
                    ship.Planet.DoForEntities( EntityRollupType.MobileCombatants, delegate ( GameEntity_Squad otherShip )
                    {
                        if ( ship.PrimaryKeyID != otherShip.PrimaryKeyID && ship.PlanetFaction.GetIsHostileTowards( otherShip.PlanetFaction )
                        && ship.CurrentTractorPullOnThis <= 0
                        && ship.GetDistanceTo_ExpensiveAccurate( otherShip.WorldLocation, true, true ) < ship.Systems[0].DataForMark.BaseRange * 0.6 )
                        {
                            otherShip.FramePlan_DoMove = true;
                            otherShip.DecollisionMoveTarget = ArcenPoint.ZeroZeroPoint;
                            int knockback = ship.GetDistanceTo_VeryCheapButExtremelyRough( otherShip.WorldLocation, true ) / 10;
                            ArcenPoint point = otherShip.WorldLocation.GetPointAtAngleAndDistance( ship.WorldLocation.GetAngleToDegrees( otherShip.WorldLocation ), knockback );
                            otherShip.FramePlan_Move_NextMovePoint = point;
                        }
                        return DelReturn.Continue;
                    } );
                    continue;
                }
                // Black Widow Golem, paralyze any ships currently being tractored by it.
                else if ( ship.TypeData.InternalName == "SKBlackWidowGolem" )
                {
                    for ( int k = 0; k < ship.CurrentlyStrongestTractorSourceHittingThese.Count; k++ )
                    {
                        int id = ship.CurrentlyStrongestTractorSourceHittingThese[k];
                        GameEntity_Squad tractoredEntity = World_AIW2.Instance.GetEntityByID_Squad( id );
                        if ( tractoredEntity == null )
                            continue;

                        tractoredEntity.CurrentParalysisSeconds = 1;
                    }
                }
            }

            // Virion fleet checking. If a ship beyond the Virion Mini ever dies, remove its capacity and give it back to the mini's capacity.
            for ( int x = 0; x < virionCenterpieces.Count; x++ )
            {
                GameEntity_Squad virionCenterpiece = World_AIW2.Instance.GetEntityByID_Squad( virionCenterpieces[x] );
                if ( virionCenterpiece == null )
                    continue;
                virionCenterpiece.FleetMembership.Fleet.DoForMemberGroups( delegate ( Fleet.Membership membership )
                 {
                     int freeCap = membership.ExplicitBaseSquadCap - membership.Entities.Count;
                     if ( freeCap > 0 )
                         switch ( membership.TypeData.InternalName )
                         {
                             case "SKVirion2":
                                 virionCenterpiece.FleetMembership.Fleet.GetOrAddMembershipGroupBasedOnSquadType_AssumeNoDuplicates(
                                     GameEntityTypeDataTable.Instance.GetRowByName( "SKVirion1", false, null ) ).ExplicitBaseSquadCap += freeCap * 2;
                                 membership.ExplicitBaseSquadCap -= freeCap;
                                 if ( membership.ExplicitBaseSquadCap == 0 )
                                     membership.Fleet.MemberGroups.Remove( membership );
                                 break;
                             case "SKVirion3":
                                 virionCenterpiece.FleetMembership.Fleet.GetOrAddMembershipGroupBasedOnSquadType_AssumeNoDuplicates(
                                     GameEntityTypeDataTable.Instance.GetRowByName( "SKVirion1", false, null ) ).ExplicitBaseSquadCap += freeCap * 4;
                                 membership.ExplicitBaseSquadCap -= freeCap;
                                 if ( membership.ExplicitBaseSquadCap == 0 )
                                     membership.Fleet.MemberGroups.Remove( membership );
                                 break;
                             case "SKVirion4":
                                 virionCenterpiece.FleetMembership.Fleet.GetOrAddMembershipGroupBasedOnSquadType_AssumeNoDuplicates(
                                     GameEntityTypeDataTable.Instance.GetRowByName( "SKVirion1", false, null ) ).ExplicitBaseSquadCap += freeCap * 8;
                                 membership.ExplicitBaseSquadCap -= freeCap;
                                 if ( membership.ExplicitBaseSquadCap == 0 )
                                     membership.Fleet.MemberGroups.Remove( membership );
                                 break;
                             case "SKVirion5":
                                 virionCenterpiece.FleetMembership.Fleet.GetOrAddMembershipGroupBasedOnSquadType_AssumeNoDuplicates(
                                     GameEntityTypeDataTable.Instance.GetRowByName( "SKVirion1", false, null ) ).ExplicitBaseSquadCap += freeCap * 16;
                                 membership.ExplicitBaseSquadCap -= freeCap;
                                 if ( membership.ExplicitBaseSquadCap == 0 )
                                     membership.Fleet.MemberGroups.Remove( membership );
                                 break;
                             default:
                                 break;
                         }

                     return DelReturn.Continue;
                 } );
            }
        }

        // Called once every 5 seconds, as defined at the start of our faction class.
        // Do NOT directly change anything from this function. Doing so may cause desyncs in multiplayer.
        // What you can do from here is queue up game commands for units, and send them to be done via QueueCommandForSendingAtEndOfContext.
        public override void DoLongRangePlanning_OnBackgroundNonSimThread_Subclass( Faction faction, ArcenLongTermIntermittentPlanningContext Context )
        {

        }
    }
}