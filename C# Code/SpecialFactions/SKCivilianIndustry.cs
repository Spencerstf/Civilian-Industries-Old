using Arcen.AIW2.Core;
using Arcen.AIW2.External;
using Arcen.Universal;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Arcen.AIW2.SK
{
    // Enum used to keep track of what our cargo and trade ships are doing.
    // Bare basic, used mostly for performance sake, so only ships that need to be processed for something are even considered valid targets.
    public enum CivilianShipStatus
    {
        Idle,           // Doing nothing.
        Loading,        // Loading resources into ship.
        Unloading,      // Offloading resources onto a station.
        Building,       // Offloading resources onto a militia building.
        Pathing,        // Pathing towards a requesting station.
        Enroute         // Taking resource to another trade station.
    }

    // Used for militia ships for most of the same reason as the above.
    // Slightly more potential actions however.
    public enum CivilianMilitiaStatus
    {
        Idle,               // Doing nothing.
        PathingForWormhole, // Pathing towards a trade station.
        PathingForMine,     // Pathing towards a trade station.
        EnrouteWormhole,    // Moving into position next to a wormhole to deploy.
        EnrouteMine,        // Moving into position next to a mine to deploy.
        Defending,          // In station form, requesting resources and building static defenses.
        Patrolling          // A more mobile form of Defense, requests resources to build mobile strike fleets.
    }

    // Enum used to keep track of resources used in this mod.
    public enum CivilianResource
    {
        Steel,
        Crystal,
        Aluminum,
        Silicon,
        Length
    }

    // Enum used to keep track of what ship requires what resource.
    public enum CivilianMilitiaShipType
    {
        PikeCorvette,
        Eyebot,
        SentinelGunboat,
        Zapper,
        Length
    }

    // Enum used to keep track of what turret requires what resource.
    public enum CivilianMilitiaTurretType
    {
        AIPikeTurret,
        AINucleophilicTurret,
        AITritiumSniperTurret,
        AIFortifiedTeslaTurret,
        Length
    }



    // World storage class. Everything can be found from here.
    public class CivilianWorld
    {
        // Faction indexes with an active civilian industry.
        public List<int> Factions;

        // Helper function(s).
        // Get the faction that the sent index is for.
        public (bool valid, Faction faction, CivilianFaction factionData) getFactionInfo( int index )
        {
            Faction faction = World_AIW2.Instance.GetFactionByIndex( this.Factions[index] );
            if ( faction == null )
            {
                ArcenDebugging.SingleLineQuickDebug( "Civilian Industries - Failed to find faction for sent index." );
                return (false, null, null);
            }
            CivilianFaction factionData = faction.GetCivilianFactionExt();
            if ( factionData == null )
            {
                ArcenDebugging.SingleLineQuickDebug( "Civilian Industries - Failed to load faction data for found faction: " + faction.GetDisplayName() );
                return (false, faction, null);
            }
            return (true, faction, factionData);
        }

        // Following three functions are used for initializing, saving, and loading data.
        // Initialization function.
        // Default values. Called on creation, NOT on load.
        public CivilianWorld()
        {
            this.Factions = new List<int>();
        }
        // Saving our data.
        public void SerializeTo( ArcenSerializationBuffer Buffer )
        {
            // Lists require a special touch to save.
            // Get the number of items in the list, and store that as well.
            // This is so you know how many items you'll have to load later.
            int count = this.Factions.Count;
            Buffer.AddItem( count );
            for ( int x = 0; x < count; x++ )
            {
                Buffer.AddItem( this.Factions[x] );
            }
        }
        // Loading our data. Make sure the loading order is the same as the saving order.
        public CivilianWorld( ArcenDeserializationBuffer Buffer )
        {
            // Lists require a special touch to load.
            // We'll have saved the number of items stored up above to be used here to determine the number of items to load.
            // ADDITIONALLY we'll need to recreate a blank list beforehand, as loading does not call the Initialization function.
            // Can't add values to a list that doesn't exist, after all.
            this.Factions = new List<int>();
            int count = Buffer.ReadInt32();
            for ( int x = 0; x < count; x++ )
            {
                this.Factions.Add( Buffer.ReadInt32() );
            }
        }
    }

    // Individual storage class for each faction.
    public class CivilianFaction
    {
        // All values stored are the index value of ships. This is done as to simply the process of saving and loading.
        // We index all of our faction ships so that they can be easily looped through based on what we're doing.

        // Index of this faction's Grand Station.
        public int GrandStation;

        // Index of all trade stations that belong to this faction.
        public List<int> TradeStations;

        // Index of all cargo ships that belong to this faction.
        public List<int> CargoShips;

        // Index of all Militia Capital Ships and/or Militia Buildings
        public List<int> MilitiaLeaders;

        // Counter used to determine when another cargo ship should be built.
        public int BuildCounter;

        // Counter used to determine when another militia ship should be built.
        public int MilitiaCounter;

        // Unlike every other value, the follow values are not stored and saved. They are simply regenerated whenever needed.
        // Contains the calculated threat value on every planet.
        // Calculated threat is all hostile strength - all friendly (excluding our own) strength.
        public List<ThreatReport> ThreatReports;

        // Get the threat value for a planet.
        public (int Militia, int Friendly, int Hostile, int Actual) GetThreat( Planet planet )
        {
            // If reports aren't generated, return 0.
            if ( ThreatReports == null )
                return (0, 0, 0, 0);
            else
                return (from o in ThreatReports where o.Planet == planet select o.GetThreat()).FirstOrDefault();
        }
        // Calculate threat values for all planets trade stations are either on or adjacent to.
        public void CalculateThreat( Faction faction, Faction playerFaction )
        {
            // Empty our dictionary.
            ThreatReports = new List<ThreatReport>();

            // Get the grand station's planet, to easily figure out when we're processing the home planet.
            GameEntity_Squad grandStation = World_AIW2.Instance.GetEntityByID_Squad( GrandStation );
            if ( grandStation == null )
                return;
            Planet grandPlanet = grandStation.Planet;
            if ( grandPlanet == null )
                return;

            // For every planet the player faction controls, process it and its neighbors.
            playerFaction.DoForControlledPlanets( delegate ( Planet basePlanet )
            {
                basePlanet.DoForLinkedNeighborsAndSelf( delegate ( Planet planet )
                {
                    // Stop if its already processed.
                    if ( (from o in ThreatReports select o.Planet).Contains( planet ) )
                        return DelReturn.Continue;

                    // Prepare variables to hold our soon to be detected threat values.
                    int friendlyStrength = 0, hostileStrength = 0, militiaStrength = 0;
                    // Wave detection.
                    for ( int j = 0; j < World_AIW2.Instance.AIFactions.Count; j++ )
                    {
                        Faction aiFaction = World_AIW2.Instance.AIFactions[j];
                        List<PlannedWave> QueuedWaves = aiFaction.GetWaveList();
                        for ( int k = 0; k < QueuedWaves.Count; k++ )
                        {
                            PlannedWave wave = QueuedWaves[k];

                            if ( wave.targetPlanetIdx != planet.PlanetIndex )
                                continue;
                            if ( wave.gameTimeInSecondsForLaunchWave - World_AIW2.Instance.GameSecond <= 90 )
                                hostileStrength += wave.CalculateStrengthOfWave( aiFaction ) * 2;
                        }
                    }
                    // Get hostile strength.
                    LongRangePlanningData_PlanetFaction linkedPlanetFactionData = planet.LongRangePlanningData.PlanetFactionDataByIndex[faction.FactionIndex];
                    LongRangePlanning_StrengthData_PlanetFaction_Stance hostileStrengthData = linkedPlanetFactionData.DataByStance[FactionStance.Hostile];
                    // If on friendly planet, triple the threat.
                    if ( planet.GetControllingFaction() == playerFaction )
                        hostileStrength += hostileStrengthData.TotalStrength * 3;
                    else
                        hostileStrength += hostileStrengthData.TotalStrength;

                    // Adjacent planet threat matters as well, but not as much as direct threat.
                    if ( planet.GetControllingFaction() == playerFaction )
                        planet.DoForLinkedNeighbors( delegate ( Planet linkedPlanet )
                        {
                            // Get hostile strength.
                            linkedPlanetFactionData = linkedPlanet.LongRangePlanningData.PlanetFactionDataByIndex[faction.FactionIndex];
                            hostileStrengthData = linkedPlanetFactionData.DataByStance[FactionStance.Hostile];
                            hostileStrength += hostileStrengthData.NonGuardMobileStrength;

                            // Slightly lower effective militia strength for each adjacent planet, based on intensity
                            militiaStrength -= (int)(GetCap() * Math.Pow( 10, faction.Ex_MinorFactionCommon_GetPrimitives().Intensity ));

                            return DelReturn.Continue;
                        } );

                    // If on home plant, double the total threat.
                    if ( planet.PlanetIndex == grandPlanet.PlanetIndex )
                        hostileStrength *= 2;

                    // Get friendly strength on the planet.
                    LongRangePlanningData_PlanetFaction planetFactionData = planet.LongRangePlanningData.PlanetFactionDataByIndex[faction.FactionIndex];
                    LongRangePlanning_StrengthData_PlanetFaction_Stance friendlyStrengthData = planetFactionData.DataByStance[FactionStance.Friendly];
                    friendlyStrength += friendlyStrengthData.TotalStrength;

                    // For each defending militia fleet that has its focus on this planet, subtract a bit of threat.
                    for ( int x = 0; x < MilitiaLeaders.Count; x++ )
                    {
                        GameEntity_Squad militiaCapital = World_AIW2.Instance.GetEntityByID_Squad( MilitiaLeaders[x] );
                        if ( militiaCapital == null )
                            continue;
                        CivilianMilitia militiaData = militiaCapital.GetCivilianMilitiaExt();
                        if ( militiaData == null )
                            continue;
                        if ( militiaData.PlanetFocus == planet.PlanetIndex &&
                            (militiaData.Status == CivilianMilitiaStatus.Defending ||
                            militiaData.Status == CivilianMilitiaStatus.EnrouteWormhole ||
                            militiaData.Status == CivilianMilitiaStatus.PathingForWormhole) )
                        {
                            militiaStrength += GetCap() * 100; // Increase strength based on current capacity.
                        }
                    }

                    // Save our threat value.
                    ThreatReports.Add( new ThreatReport( planet, militiaStrength, friendlyStrength, hostileStrength ) );

                    return DelReturn.Continue;
                } );
                return DelReturn.Continue;
            } );

            // Sort our reports.
            ThreatReports.Sort();
        }

        // Returns the resource cost per ship/turret.
        public int GetResourceCost( int count )
        {
            // (50 - (Intensity ^ 1.1)) ^ (1 + #OfBuiltShips / 1000 )
            return (int)Math.Pow( 50 - (int)Math.Pow( World_AIW2.Instance.GetEntityByID_Squad( GrandStation ).PlanetFaction.Faction.Ex_MinorFactionCommon_GetPrimitives().Intensity, 1.1 ),
                1 + count / 1000 );
        }

        // Returns the current capacity for turrets/ships.
        public int GetCap()
        {
            // (20 + (AIP / 5)) ^ (1 + (Intensity/100) 
            int cap = 0;
            for ( int y = 0; y < World_AIW2.Instance.AIFactions.Count; y++ )
                cap = (int)(Math.Ceiling( Math.Pow( Math.Max( cap, 20 + World_AIW2.Instance.AIFactions[y].GetAICommonExternalData().AIProgress_Total.ToInt() / 5 ),
                     1 + (World_AIW2.Instance.GetEntityByID_Squad( GrandStation ).PlanetFaction.Faction.Ex_MinorFactionCommon_GetPrimitives().Intensity / 100) ) ));
            return cap;
        }

        // Following three functions are used for initializing, saving, and loading data.
        // Initialization function.
        // Default values. Called on creation, NOT on load.
        public CivilianFaction()
        {
            this.GrandStation = -1;
            this.TradeStations = new List<int>();
            this.CargoShips = new List<int>();
            this.MilitiaLeaders = new List<int>();
            this.BuildCounter = 0;
            this.MilitiaCounter = 0;
            this.ThreatReports = new List<ThreatReport>();
        }
        // Saving our data.
        public void SerializeTo( ArcenSerializationBuffer Buffer )
        {
            Buffer.AddItem( this.GrandStation );
            // Lists require a special touch to save.
            // Get the number of items in the list, and store that as well.
            // This is so you know how many items you'll have to load later.
            int count = this.TradeStations.Count;
            Buffer.AddItem( count );
            for ( int x = 0; x < count; x++ )
                Buffer.AddItem( this.TradeStations[x] );
            count = this.CargoShips.Count;
            Buffer.AddItem( count );
            for ( int x = 0; x < count; x++ )
                Buffer.AddItem( this.CargoShips[x] );
            count = this.MilitiaLeaders.Count;
            Buffer.AddItem( count );
            for ( int x = 0; x < count; x++ )
                Buffer.AddItem( this.MilitiaLeaders[x] );
            Buffer.AddItem( this.BuildCounter );
            Buffer.AddItem( this.MilitiaCounter );
        }
        // Loading our data. Make sure the loading order is the same as the saving order.
        public CivilianFaction( ArcenDeserializationBuffer Buffer )
        {
            this.GrandStation = Buffer.ReadInt32();
            // Lists require a special touch to load.
            // We'll have saved the number of items stored up above to be used here to determine the number of items to load.
            // ADDITIONALLY we'll need to recreate a blank list beforehand, as loading does not call the Initialization function.
            // Can't add values to a list that doesn't exist, after all.
            this.TradeStations = new List<int>();
            this.CargoShips = new List<int>();
            this.MilitiaLeaders = new List<int>();
            int count = Buffer.ReadInt32();
            for ( int x = 0; x < count; x++ )
                this.TradeStations.Add( Buffer.ReadInt32() );
            count = Buffer.ReadInt32();
            for ( int x = 0; x < count; x++ )
                this.CargoShips.Add( Buffer.ReadInt32() );
            count = Buffer.ReadInt32();
            for ( int x = 0; x < count; x++ )
                this.MilitiaLeaders.Add( Buffer.ReadInt32() );
            this.BuildCounter = Buffer.ReadInt32();
            this.MilitiaCounter = Buffer.ReadInt32();

            // Recreate an empty list of threat values on load. Will be populated when needed.
            this.ThreatReports = new List<ThreatReport>();
        }
    }

    // Used to report on the amount of threat on each planet.
    public class ThreatReport : IComparable<ThreatReport>
    {
        public Planet Planet;
        public int MilitiaStrength;
        public int FriendlyStrength;
        public int HostileStrength;
        public (int Militia, int Friendly, int Hostile, int Actual) GetThreat()
        {
            return (MilitiaStrength, FriendlyStrength, HostileStrength, HostileStrength - FriendlyStrength - MilitiaStrength);
        }

        public ThreatReport( Planet planet, int militiaStrength, int friendlyStrength, int hostileStrength )
        {
            Planet = planet;
            MilitiaStrength = militiaStrength;
            FriendlyStrength = friendlyStrength;
            HostileStrength = hostileStrength;
        }

        public int CompareTo( ThreatReport other )
        {
            // We want higher threat to be first in a list, so reverse the normal sorting order.
            return other.GetThreat().Hostile.CompareTo( this.GetThreat().Hostile );
        }
    }

    // Used to report on how strong an attack would be on a hostile planet.
    public class AttackAssessment : IComparable<AttackAssessment>
    {
        public Planet Target;
        public Dictionary<Planet, int> Attackers;
        public int StrengthRequired;
        public int AttackPower { get { return (from o in Attackers select o.Value).Sum(); } }

        public AttackAssessment( Planet target, int strengthRequired )
        {
            Target = target;
            Attackers = new Dictionary<Planet, int>();
            StrengthRequired = strengthRequired;
        }
        public int CompareTo( AttackAssessment other )
        {
            // We want higher threat to be first in a list, so reverse the normal sorting order.
            return other.AttackPower.CompareTo( this.AttackPower );
        }
    }

    // Used on any entity which has resources.
    public class CivilianCargo
    {
        // We have three arrays here.
        // One for current amount, one for capacity, and one for per second change.
        public int[] Amount;
        public int[] Capacity;
        public int[] PerSecond; // Positive is generation, negative is drain.

        // Following three functions are used for initializing, saving, and loading data.
        // Initialization function.
        // Default values. Called on creation, NOT on load.
        public CivilianCargo()
        {
            // Values are set to the default for ships. Stations will manually initialize theirs.
            this.Amount = new int[(int)CivilianResource.Length];
            this.Capacity = new int[(int)CivilianResource.Length];
            this.PerSecond = new int[(int)CivilianResource.Length];
            for ( int x = 0; x < this.Amount.Length; x++ )
            {
                this.Amount[x] = 0;
                this.Capacity[x] = 100;
                this.PerSecond[x] = 0;
            }
        }
        // Saving our data.
        public void SerializeTo( ArcenSerializationBuffer Buffer )
        {
            // Arrays
            // Get the number of items in the list, and store that as well.
            // This is so you know how many items you'll have to load later.
            // As we have one entry for each resource, we'll only have to get the count once.
            int count = this.Amount.Length;
            Buffer.AddItem( count );
            for ( int x = 0; x < count; x++ )
                Buffer.AddItem( this.Amount[x] );
            for ( int x = 0; x < count; x++ )
                Buffer.AddItem( this.Capacity[x] );
            for ( int x = 0; x < count; x++ )
                Buffer.AddItem( this.PerSecond[x] );
        }
        // Loading our data. Make sure the loading order is the same as the saving order.
        public CivilianCargo( ArcenDeserializationBuffer Buffer )
        {
            // Lists require a special touch to load.
            // We'll have saved the number of items stored up above to be used here to determine the number of items to load.
            // ADDITIONALLY we'll need to recreate our arrays beforehand, as loading does not call the Initialization function.
            // Can't add values to an array that doesn't exist, after all.
            // Its more important to be accurate than it is to be update safe here, so we'll always use our stored value to figure out the number of resources.
            int count = Buffer.ReadInt32();
            this.Amount = new int[count];
            this.Capacity = new int[count];
            this.PerSecond = new int[count];
            for ( int x = 0; x < count; x++ )
                this.Amount[x] = Buffer.ReadInt32();
            for ( int x = 0; x < count; x++ )
                this.Capacity[x] = Buffer.ReadInt32();
            for ( int x = 0; x < count; x++ )
                this.PerSecond[x] = Buffer.ReadInt32();
        }
    }

    // Used for the creation and sorting of trade requests.
    public class TradeRequest : IComparable<TradeRequest>
    {
        // The resource to be requested.
        public CivilianResource Requested;

        // The urgency of the request.
        public int Urgency;

        // Is this an Export request?
        public bool IsExport;

        // The station with this request.
        public GameEntity_Squad Station;

        // Finished being processed.
        public bool Processed;

        public TradeRequest( CivilianResource request, int urgency, bool isExport, GameEntity_Squad station )
        {
            Requested = request;
            Urgency = urgency;
            IsExport = isExport;
            Station = station;
            Processed = false;
        }

        public int CompareTo( TradeRequest other )
        {
            // We want higher urgencies to be first in a list, so reverse the normal sorting order.
            return other.Urgency.CompareTo( this.Urgency );
        }
    }

    // Used on mobile ships. Tells us what they're currently doing.
    public class CivilianStatus
    {
        // The ship's current status.
        public CivilianShipStatus Status;

        // The index of the requesting station.
        // If -1, its being sent from the grand station.
        public int Origin;

        // The index of the ship's destination station, if any.
        public int Destination;

        // The amount of time left before departing from a loading job.
        // Usually 2 minutes.
        public int LoadTimer;

        // Following three functions are used for initializing, saving, and loading data.
        // Initialization function.
        // Default values. Called on creation, NOT on load.
        public CivilianStatus()
        {
            this.Status = CivilianShipStatus.Idle;
            this.Origin = -1;
            this.Destination = -1;
            this.LoadTimer = 0;
        }
        // Saving our data.
        public void SerializeTo( ArcenSerializationBuffer Buffer )
        {
            Buffer.AddItem( (int)this.Status );
            Buffer.AddItem( this.Origin );
            Buffer.AddItem( this.Destination );
            Buffer.AddItem( this.LoadTimer );
        }
        // Loading our data. Make sure the loading order is the same as the saving order.
        public CivilianStatus( ArcenDeserializationBuffer Buffer )
        {
            this.Status = (CivilianShipStatus)Buffer.ReadInt32();
            this.Origin = Buffer.ReadInt32();
            this.Destination = Buffer.ReadInt32();
            this.LoadTimer = Buffer.ReadInt32();
        }
    }

    // Used on militia fleets. Tells us what their focus is.
    public class CivilianMilitia
    {
        // The status of the fleet.
        public CivilianMilitiaStatus Status;

        // The planet that this fleet's focused on.
        // It will only interact to hostile forces on or adjacent to this.
        public int PlanetFocus;

        // Wormhole that this fleet has been assigned to. If -1, it will instead find an unchosen mine on the planet.
        public int EntityFocus;

        // Resources stored towards ship production.
        public int[] ProcessedResources;
        public int[] MaximumCapacity;

        // Following three functions are used for initializing, saving, and loading data.
        // Initialization function.
        // Default values. Called on creation, NOT on load.
        public CivilianMilitia()
        {
            this.Status = CivilianMilitiaStatus.Idle;
            this.PlanetFocus = -1;
            this.EntityFocus = -1;
            this.ProcessedResources = new int[(int)CivilianResource.Length];
            this.MaximumCapacity = new int[(int)CivilianResource.Length];
            for ( int x = 0; x < this.ProcessedResources.Length; x++ )
            {
                this.ProcessedResources[x] = 0;
                this.MaximumCapacity[x] = 1500;
            }
        }
        // Saving our data.
        public void SerializeTo( ArcenSerializationBuffer Buffer )
        {
            Buffer.AddItem( (int)this.Status );
            Buffer.AddItem( this.PlanetFocus );
            Buffer.AddItem( this.EntityFocus );
            int count = this.ProcessedResources.Length;
            Buffer.AddItem( count );
            for ( int x = 0; x < count; x++ )
            {
                Buffer.AddItem( this.ProcessedResources[x] );
                Buffer.AddItem( this.MaximumCapacity[x] );
            }
        }
        // Loading our data. Make sure the loading order is the same as the saving order.
        public CivilianMilitia( ArcenDeserializationBuffer Buffer )
        {
            this.Status = (CivilianMilitiaStatus)Buffer.ReadInt32();
            this.PlanetFocus = Buffer.ReadInt32();
            this.EntityFocus = Buffer.ReadInt32();
            this.ProcessedResources = new int[(int)CivilianResource.Length];
            this.MaximumCapacity = new int[(int)CivilianResource.Length];
            int count = Buffer.ReadInt32();
            for ( int x = 0; x < count; x++ )
            {
                this.ProcessedResources[x] = Buffer.ReadInt32();
                this.MaximumCapacity[x] = Buffer.ReadInt32();
            }
        }

        public GameEntity_Squad getMine()
        {
            return World_AIW2.Instance.GetEntityByID_Squad( this.EntityFocus );
        }

        public GameEntity_Other getWormhole()
        {
            return World_AIW2.Instance.GetEntityByID_Other( this.EntityFocus );
        }
    }

    // Description classes.
    // Grand Stations
    // Used to display faction-related info to the player.
    public class GrandStationDescriptionAppender : IGameEntityDescriptionAppender
    {
        public void AddToDescriptionBuffer( GameEntity_Squad RelatedEntityOrNull, GameEntityTypeData RelatedEntityTypeData, ArcenDoubleCharacterBuffer Buffer )
        {
            // Make sure we are getting an entity.
            if ( RelatedEntityOrNull == null )
                return;
            // Need to find our faction data to display information.
            // Look through our world data, first, to find which faction controls our starting station, and load its faction data.
            CivilianWorld worldData = World.Instance.GetCivilianWorldExt();
            CivilianFaction factionData = null;
            // Look through our saved factions to find which one has our starting station
            for ( int x = 0; x < worldData.Factions.Count; x++ )
            {
                var tempData = worldData.getFactionInfo( x );
                if ( tempData.factionData.GrandStation == RelatedEntityOrNull.PrimaryKeyID )
                {
                    factionData = tempData.factionData;
                }
            }

            // If we found our faction data, inform them about build requests in the faction.
            if ( factionData != null )
            {
                Buffer.Add( "\n" + factionData.BuildCounter + "/" + (factionData.GetCap() + factionData.GetResourceCost( factionData.CargoShips.Count )) + " Request points until next Cargo Ship built." );
                Buffer.Add( "\n" + factionData.MilitiaCounter + "/" + (factionData.GetCap() + factionData.GetResourceCost( factionData.MilitiaLeaders.Count )) + " Request points until next Miltia ship built." );
            }

            // Add in an empty line to stop any other gunk (such as the fleet display) from messing up our given information.
            Buffer.Add( "\n" );
            return;
        }
    }

    // Trade Stations
    // Used to display stored cargo
    public class TradeStationDescriptionAppender : IGameEntityDescriptionAppender
    {
        public void AddToDescriptionBuffer( GameEntity_Squad RelatedEntityOrNull, GameEntityTypeData RelatedEntityTypeData, ArcenDoubleCharacterBuffer Buffer )
        {
            // Make sure we are getting an entity.
            if ( RelatedEntityOrNull == null )
                return;
            // Load our cargo data.
            CivilianCargo cargoData = RelatedEntityOrNull.GetCivilianCargoExt();

            // Load the faction data of the this trade station.
            // Look through our world data, first, to find which faction controls our trade station, and load its faction data.
            CivilianWorld worldData = World.Instance.GetCivilianWorldExt();

            // Inform them about what the station has on it.
            for ( int x = 0; x < cargoData.Amount.Length; x++ )
            {
                if ( cargoData.Amount[x] > 0 || cargoData.PerSecond[x] != 0 )
                    Buffer.Add( "\n" + cargoData.Amount[x] + "/" + cargoData.Capacity[x] + " " + ((CivilianResource)x).ToString() );
                // If resource has generation or drain, notify them.
                if ( cargoData.PerSecond[x] != 0 )
                    Buffer.Add( " +" + cargoData.PerSecond[x] + " per second" );
            }

            // Add in an empty line to stop any other gunk (such as the fleet display) from messing up our given information.
            Buffer.Add( "\n" );
            return;
        }
    }

    // Cargo Ships
    // Used to display stored cargo and the ship's status
    public class CargoShipDescriptionAppender : IGameEntityDescriptionAppender
    {
        public void AddToDescriptionBuffer( GameEntity_Squad RelatedEntityOrNull, GameEntityTypeData RelatedEntityTypeData, ArcenDoubleCharacterBuffer Buffer )
        {
            // Make sure we are getting an entity.
            if ( RelatedEntityOrNull == null )
                return;
            // Load our cargo data.
            CivilianCargo cargoData = RelatedEntityOrNull.GetCivilianCargoExt();
            // Load our status data.
            CivilianStatus shipStatus = RelatedEntityOrNull.GetCivilianStatusExt();

            // Inform them about what the ship is doing.
            Buffer.Add( "\nThis ship is currently " + shipStatus.Status.ToString() );
            // If currently pathing or enroute, continue to explain towards where
            if ( shipStatus.Status == CivilianShipStatus.Enroute )
                Buffer.Add( " towards " + World_AIW2.Instance.GetEntityByID_Squad( shipStatus.Destination ).GetQualifiedName() + " on planet " + World_AIW2.Instance.GetEntityByID_Squad( shipStatus.Destination ).Planet.Name );
            if ( shipStatus.Status == CivilianShipStatus.Pathing )
                Buffer.Add( " towards " + World_AIW2.Instance.GetEntityByID_Squad( shipStatus.Origin ).Planet.Name );
            // Inform them about what the ship has on it.
            for ( int x = 0; x < cargoData.Amount.Length; x++ )
                Buffer.Add( "\n" + cargoData.Amount[x] + "/" + cargoData.Capacity[x] + " " + ((CivilianResource)x).ToString() );
            // Add in an empty line to stop any other gunk (such as the fleet display) from messing up our given information.
            Buffer.Add( "\n" );
            return;
        }
    }

    // Miltia Ships
    // Used to display defensive focuses and ship's status.
    public class MilitiaShipDescriptionAppender : IGameEntityDescriptionAppender
    {
        public void AddToDescriptionBuffer( GameEntity_Squad RelatedEntityOrNull, GameEntityTypeData RelatedEntityTypeData, ArcenDoubleCharacterBuffer Buffer )
        {
            // Make sure we are getting an entity.
            if ( RelatedEntityOrNull == null )
                return;
            // Load our militia data
            CivilianMilitia militiaData = RelatedEntityOrNull.GetCivilianMilitiaExt();

            // In order to find our player faction (which we'll need to display the ship capacity, as its based on aip)
            // We'll have to load our world data.
            CivilianWorld worldData = World.Instance.GetCivilianWorldExt();
            Faction playerFaction;
            CivilianFaction factionData = null;
            // Look through our saved factions to find which one has our militia ship
            for ( int x = 0; x < worldData.Factions.Count; x++ )
            {
                CivilianFaction tempData = worldData.getFactionInfo( x ).factionData;
                if ( tempData.MilitiaLeaders.Contains( RelatedEntityOrNull.PrimaryKeyID ) )
                {
                    playerFaction = worldData.getFactionInfo( x ).faction;
                    factionData = playerFaction.GetCivilianFactionExt();
                }
            }

            if ( factionData == null )
                return;

            for ( int x = 0; x < militiaData.ProcessedResources.Length; x++ )
            {
                // Forces information.
                GameEntityTypeData typeData = null;
                if ( militiaData.Status == CivilianMilitiaStatus.Defending )
                    typeData = GameEntityTypeDataTable.Instance.GetRowByName( ((CivilianMilitiaTurretType)x).ToString(), false, null );
                else if ( militiaData.Status == CivilianMilitiaStatus.Patrolling )
                    typeData = GameEntityTypeDataTable.Instance.GetRowByName( ((CivilianMilitiaShipType)x).ToString(), false, null );
                int count = 0;
                try
                {
                    List<SquadWrapper> squads = RelatedEntityOrNull.FleetMembership.Fleet.GetButDoNotAddMembershipGroupBasedOnSquadType_AssumeNoDuplicates( typeData ).Entities;
                    for ( int y = 0; y < squads.Count; y++ )
                        count += 1 + squads[y].GetSquad().ExtraStackedSquadsInThis;
                }
                catch ( Exception )
                {
                    count = 0;
                }

                // If one is built, or it is in the process of building one, display.
                if ( count > 0 || militiaData.ProcessedResources[x] > 0 )
                {
                    Buffer.Add( "\n" + count + "/" + factionData.GetCap() + " " + typeData.DisplayName );
                    // If at least one metal, list time until next.
                    if ( count >= factionData.GetCap() )
                        Buffer.Add( " (Building paused, max capacity.)" );
                    else if ( militiaData.ProcessedResources[x] > 0 )
                        Buffer.Add( " (Processing " + ((CivilianResource)x).ToString() + ", " + ((factionData.GetResourceCost( count )) - militiaData.ProcessedResources[x]) + " " + ((CivilianResource)x).ToString() + " left.)" );
                }
            }

            // Inform them about any focus the ship may have.
            if ( militiaData.PlanetFocus != -1 )
                Buffer.Add( " This ship's planetary focus is " + World_AIW2.Instance.GetPlanetByIndex( militiaData.PlanetFocus ).Name );

            // Add in an empty line to stop any other gunk (such as the fleet display) from messing up our given information.
            Buffer.Add( "\n" );
            return;
        }
    }

    // The main faction class.
    public class SpecialFaction_SKCivilianIndustry : BaseSpecialFaction
    {
        // Information required for our faction.
        // General identifier for our faction.
        protected override string TracingName => "SKCivilianIndustry";

        // Let the game know we're going to want to use the DoLongRangePlanning_OnBackgroundNonSimThread_Subclass function.
        // This function is generally used for things that do not need to always run, such as navigation requests.
        protected override bool EverNeedsToRunLongRangePlanning => true;

        // How often our DoLongRangePlanning_OnBackgroundNonSimThread_Subclass function is called in seconds.
        protected override FInt MinimumSecondsBetweenLongRangePlannings => FInt.FromParts( 5, 000 );

        // Set up initial relationships.
        public override void SetStartingFactionRelationships( Faction faction )
        {
            base.SetStartingFactionRelationships( faction );
            for ( int i = 0; i < World_AIW2.Instance.Factions.Count; i++ ) // Go through a list of all factions in the game.
            {
                Faction otherFaction = World_AIW2.Instance.Factions[i];
                if ( faction == otherFaction ) // Found ourself.
                    continue; // Shun ourself.
                switch ( otherFaction.Type )
                {
                    case FactionType.AI: // Hostile to AI.
                        faction.MakeHostileTo( otherFaction );
                        otherFaction.MakeHostileTo( faction );
                        break;
                    case FactionType.SpecialFaction: // Hostile to other non player factions.
                        faction.MakeHostileTo( otherFaction );
                        otherFaction.MakeHostileTo( faction );
                        break;
                    case FactionType.Player: // Friendly to players. This entire faction is used for all players, and should thus be friendly to all.
                        faction.MakeFriendlyTo( otherFaction );
                        otherFaction.MakeFriendlyTo( faction );
                        break;
                }
            }
        }

        // Handle the creation of the Grand Station.
        public void CreateGrandStation( Faction faction, Faction playerFaction, CivilianFaction factionData, ArcenSimContext Context )
        {
            // Look through the game's list of king units.
            World_AIW2.Instance.DoForEntities( EntityRollupType.KingUnitsOnly, delegate ( GameEntity_Squad kingEntity )
             {
                 // Make sure its the correct faction.
                 if ( kingEntity.PlanetFaction.Faction.FactionIndex != playerFaction.FactionIndex )
                     return DelReturn.Continue;

                 // Load in our Grand Station's TypeData.
                 GameEntityTypeData entityData = GameEntityTypeDataTable.Instance.GetRandomRowWithTag( Context, "GrandStation" );

                 // Get the total radius of both our grand station and the king unit.
                 // This will be used to find a safe spawning location.
                 int radius = entityData.ForMark[Balance_MarkLevelTable.MaxOrdinal].Radius + kingEntity.TypeData.ForMark[Balance_MarkLevelTable.MaxOrdinal].Radius;

                 // Get the spawning coordinates for our start station.
                 ArcenPoint spawnPoint = ArcenPoint.ZeroZeroPoint;
                 int outerMax = 0;
                 do
                 {
                     outerMax++;
                     spawnPoint = kingEntity.Planet.GetSafePlacementPoint( Context, entityData, kingEntity.WorldLocation, radius, radius * outerMax );
                 } while ( spawnPoint == ArcenPoint.ZeroZeroPoint );

                 // Get the planetary faction to spawn our station in as.
                 PlanetFaction pFaction = kingEntity.Planet.GetPlanetFactionForFaction( faction );

                 // Spawn in the station.
                 GameEntity_Squad grandStation = GameEntity_Squad.CreateNew( pFaction, entityData, entityData.MarkFor( pFaction ), pFaction.FleetUsedAtPlanet, 0, spawnPoint, Context );

                 // Add in our grand station to our faction's data
                 factionData.GrandStation = grandStation.PrimaryKeyID;

                 return DelReturn.Break;
             } );
        }

        // Handle creation of trade stations.
        public void CreateTradeStations( Faction faction, Faction playerFaction, CivilianFaction factionData, ArcenSimContext Context )
        {
            playerFaction.Entities.DoForEntities( EntityRollupType.CommandStation, delegate ( GameEntity_Squad commandStation )
             {
                 // Skip if its not currently ready.
                 if ( commandStation.SecondsSpentAsRemains > 0 )
                     return DelReturn.Continue;
                 if ( commandStation.RepairDelaySeconds > 0 )
                     return DelReturn.Continue;
                 if ( commandStation.SelfBuildingMetalRemaining > FInt.Zero )
                     return DelReturn.Continue;

                 // Get the commandStation's planet.
                 Planet planet = commandStation.Planet;
                 if ( planet == null )
                     return DelReturn.Continue;

                 // Skip if we already have a trade station on the planet.
                 for ( int x = 0; x < factionData.TradeStations.Count; x++ )
                 {
                     GameEntity_Squad station = World_AIW2.Instance.GetEntityByID_Squad( factionData.TradeStations[x] );
                     if ( station == null )
                         continue;
                     if ( station.Planet.PlanetIndex == planet.PlanetIndex )
                         return DelReturn.Continue;
                 }

                 // No trade station found for this planet. Create one.
                 // Load in our trade station's data.
                 GameEntityTypeData entityData = GameEntityTypeDataTable.Instance.GetRandomRowWithTag( Context, "TradeStation" );

                 // Get the total radius of both our trade station, and the command station.
                 // This will be used to find a safe spawning location.
                 int radius = entityData.ForMark[Balance_MarkLevelTable.MaxOrdinal].Radius + commandStation.TypeData.ForMark[Balance_MarkLevelTable.MaxOrdinal].Radius;

                 // Get the spawning coordinates for our trade station.
                 ArcenPoint spawnPoint = ArcenPoint.ZeroZeroPoint;
                 int outerMax = 0;
                 do
                 {
                     outerMax++;
                     spawnPoint = planet.GetSafePlacementPoint( Context, entityData, commandStation.WorldLocation, radius, radius * outerMax );
                 } while ( spawnPoint == ArcenPoint.ZeroZeroPoint );

                 // Get the planetary faction to spawn our trade station in as.
                 PlanetFaction pFaction = planet.GetPlanetFactionForFaction( faction );

                 // Spawn in the station.
                 GameEntity_Squad tradeStation = GameEntity_Squad.CreateNew( pFaction, entityData, entityData.MarkFor( pFaction ), pFaction.FleetUsedAtPlanet, 0, spawnPoint, Context );

                 // Add in our trade station to our faction's data
                 factionData.TradeStations.Add( tradeStation.PrimaryKeyID );

                 // Initialize cargo.
                 CivilianCargo tradeCargo = tradeStation.GetCivilianCargoExt();
                 // Large capacity.
                 for ( int y = 0; y < tradeCargo.Capacity.Length; y++ )
                     tradeCargo.Capacity[y] *= 25;
                 // Pick a random resource, and generate it based on mine count.
                 int mines = 0;
                 tradeStation.Planet.DoForEntities( EntityRollupType.MetalProducers, delegate ( GameEntity_Squad mineEntity )
                 {
                     if ( mineEntity.TypeData.GetHasTag( "MetalGenerator" ) )
                         mines++;

                     return DelReturn.Continue;
                 } );
                 tradeCargo.PerSecond[Context.RandomToUse.Next( (int)CivilianResource.Length )] = (int)Math.Pow( mines, 1.5 );

                 tradeStation.SetCivilianCargoExt( tradeCargo );

                 return DelReturn.Continue;
             } );
        }

        // Handle resource processing.
        public void DoResources( Faction faction, Faction playerFaction, CivilianFaction factionData, ArcenSimContext Context )
        {
            // For every TradeStation we have defined in our faction data, deal with it.
            for ( int x = 0; x < factionData.TradeStations.Count; x++ )
            {
                // Load the entity, and its cargo data.
                GameEntity_Squad entity = World_AIW2.Instance.GetEntityByID_Squad( factionData.TradeStations[x] );
                if ( entity == null )
                {
                    factionData.TradeStations.RemoveAt( x );
                    x--;
                    continue;
                }
                CivilianCargo entityCargo = entity.GetCivilianCargoExt();

                // Deal with its per second values.
                for ( int y = 0; y < entityCargo.PerSecond.Length; y++ )
                {
                    if ( entityCargo.PerSecond[y] != 0 )
                    {
                        // Update the resource, if able.
                        if ( entityCargo.PerSecond[y] > 0 || (entityCargo.Amount[y] >= Math.Abs( entityCargo.PerSecond[y] )) )
                        {
                            entityCargo.Amount[y] = Math.Min( entityCargo.Capacity[y], entityCargo.Amount[y] + entityCargo.PerSecond[y] );
                        }
                    }
                }

                // Save its resources.
                entity.SetCivilianCargoExt( entityCargo );
            }
        }

        // Handle the creation of ships.
        public void DoShipSpawns( Faction faction, Faction playerFaction, CivilianFaction factionData, ArcenSimContext Context )
        {
            // Continue only if starting station is valid.
            if ( factionData.GrandStation == -1 || factionData.GrandStation == -2 )
                return;

            // Load our grand station.
            GameEntity_Squad grandStation = World_AIW2.Instance.GetEntityByID_Squad( factionData.GrandStation );

            // Build a cargo ship if we have enough requests for them.
            if ( factionData.CargoShips.Count < 10 || factionData.BuildCounter > factionData.GetCap() + factionData.GetResourceCost( factionData.CargoShips.Count ) )
            {
                // Load our cargo ship's data.
                GameEntityTypeData entityData = GameEntityTypeDataTable.Instance.GetRandomRowWithTag( Context, "CargoShip" );

                // Get the planet faction to spawn it in as.
                PlanetFaction pFaction = grandStation.Planet.GetPlanetFactionForFaction( faction );

                // Get the spawning coordinates for our cargo ship.
                // We'll simply spawn it right on top of our grand station, and it'll dislocate itself.
                ArcenPoint spawnPoint = grandStation.WorldLocation;

                // Spawn in the ship.
                GameEntity_Squad entity = GameEntity_Squad.CreateNew( pFaction, entityData, entityData.MarkFor( pFaction ), pFaction.FleetUsedAtPlanet, 0, spawnPoint, Context );

                // Add the cargo ship to our faction data.
                factionData.CargoShips.Add( entity.PrimaryKeyID );

                // Reset the build counter.
                factionData.BuildCounter = 0;
            }

            // Build mitia ship if we have enough requets for them.
            if ( factionData.MilitiaLeaders.Count < 1 || factionData.MilitiaCounter > factionData.GetCap() + factionData.GetResourceCost( factionData.MilitiaLeaders.Count ) )
            {
                // Load our militia ship's data.
                GameEntityTypeData entityData = GameEntityTypeDataTable.Instance.GetRandomRowWithTag( Context, "MilitiaCapitalShip" );

                // Get the planet faction to spawn it in as.
                PlanetFaction pFaction = grandStation.Planet.GetPlanetFactionForFaction( faction );

                // Get the spawning coordinates for our militia ship.
                // We'll simply spawn it right on top of our grand station, and it'll dislocate itself.
                ArcenPoint spawnPoint = grandStation.WorldLocation;

                // Spawn in the ship.
                GameEntity_Squad entity = GameEntity_Squad.CreateNew( pFaction, entityData, entityData.MarkFor( pFaction ), pFaction.FleetUsedAtPlanet, 0, spawnPoint, Context );

                // Add the militia ship to our faction data.
                factionData.MilitiaLeaders.Add( entity.PrimaryKeyID );

                // Reset the build counter.
                factionData.MilitiaCounter = 0;
            }
        }

        // Check for ship arrival.
        public void DoShipArrival( Faction faction, Faction playerFaciton, CivilianFaction factionData, ArcenSimContext Context )
        {
            // Loop through every cargo ship.
            for ( int x = 0; x < factionData.CargoShips.Count; x++ )
            {
                // Get the ship.
                GameEntity_Squad cargoShip = World_AIW2.Instance.GetEntityByID_Squad( factionData.CargoShips[x] );
                if ( cargoShip == null )
                    continue;

                // Load the cargo ship's status.
                CivilianStatus shipStatus = cargoShip.GetCivilianStatusExt();

                // Pathing logic, detect arrival at trade station.
                if ( shipStatus.Status == CivilianShipStatus.Enroute )
                {
                    // Heading towards destination station
                    // Confirm its destination station still exists.
                    GameEntity_Squad destinationStation = World_AIW2.Instance.GetEntityByID_Squad( shipStatus.Destination );

                    // If station not found, idle the cargo ship.
                    if ( destinationStation == null )
                    {
                        shipStatus.Status = CivilianShipStatus.Idle;
                        cargoShip.SetCivilianStatusExt( shipStatus );
                        continue;
                    }

                    // If ship not at destination planet yet, do nothing.
                    if ( cargoShip.Planet.PlanetIndex != destinationStation.Planet.PlanetIndex )
                        continue;

                    // If ship is close to destination station, start unloading.
                    if ( cargoShip.GetDistanceTo_ExpensiveAccurate( destinationStation.WorldLocation, true, true ) < 2000 )
                    {
                        if ( destinationStation.TypeData.GetHasTag( "TradeStation" ) )
                            shipStatus.Status = CivilianShipStatus.Unloading;
                        else
                            shipStatus.Status = CivilianShipStatus.Building;
                        shipStatus.LoadTimer = 120;
                        cargoShip.SetCivilianStatusExt( shipStatus );
                    }
                }
                else if ( shipStatus.Status == CivilianShipStatus.Pathing )
                {
                    // Heading towads origin station.
                    // Confirm its origin station still exists.
                    GameEntity_Squad originStation = World_AIW2.Instance.GetEntityByID_Squad( shipStatus.Origin );

                    // If station not found, idle the cargo ship.
                    if ( originStation == null )
                    {
                        shipStatus.Status = CivilianShipStatus.Idle;
                        cargoShip.SetCivilianStatusExt( shipStatus );
                        continue;
                    }

                    // If ship not at origin planet yet, do nothing.
                    if ( cargoShip.Planet.PlanetIndex != originStation.Planet.PlanetIndex )
                        continue;

                    // If ship is close to origin station, start loading.
                    if ( cargoShip.GetDistanceTo_ExpensiveAccurate( originStation.WorldLocation, true, true ) < 2000 )
                    {
                        shipStatus.Status = CivilianShipStatus.Loading;
                        shipStatus.LoadTimer = 120;
                        cargoShip.SetCivilianStatusExt( shipStatus );
                    }
                }
            }
        }

        // Handle resource transferring.
        public void DoResourceTransfer( Faction faction, Faction playerFaction, CivilianFaction factionData, ArcenSimContext Context )
        {
            // Loop through every cargo ship.
            for ( int x = 0; x < factionData.CargoShips.Count; x++ )
            {
                // Get the ship.
                GameEntity_Squad cargoShip = World_AIW2.Instance.GetEntityByID_Squad( factionData.CargoShips[x] );
                if ( cargoShip == null )
                    continue;

                // Load the cargo ship's status.
                CivilianStatus shipStatus = cargoShip.GetCivilianStatusExt();

                // Handle resource loading.
                if ( shipStatus.Status == CivilianShipStatus.Loading )
                {
                    // Decrease its wait timer.
                    shipStatus.LoadTimer--;

                    // Load the cargo ship's cargo.
                    CivilianCargo shipCargo = cargoShip.GetCivilianCargoExt();

                    // Load the origin station and its cargo.
                    GameEntity_Squad originStation = World_AIW2.Instance.GetEntityByID_Squad( shipStatus.Origin );
                    // If the station has died, free the cargo ship.
                    if ( originStation == null )
                    {
                        shipStatus.Status = CivilianShipStatus.Idle;
                        cargoShip.SetCivilianStatusExt( shipStatus );
                        continue;
                    }
                    CivilianCargo originCargo = originStation.GetCivilianCargoExt();
                    bool isFinished = true;

                    // Send the resources, if the station has any left.
                    for ( int y = 0; y < (int)CivilianResource.Length; y++ )
                    {
                        // If its a resource we only store, take out or put in based on its current amount. Balance as all things should be.
                        // In other words, aim for 50% of the resource stored on both the station and ship, unless its a resource it produces.
                        // If its produced, take as much as we can to spread elsewhere.
                        if ( originCargo.PerSecond[y] <= 0 )
                        {
                            if ( originCargo.Amount[y] < originCargo.Capacity[y] / 2 && shipCargo.Amount[y] > shipCargo.Capacity[y] / 2 )
                            {
                                shipCargo.Amount[y]--;
                                originCargo.Amount[y]++;
                            }
                            else if ( originCargo.Amount[y] > 0 && shipCargo.Amount[y] < shipCargo.Capacity[y] )
                            {
                                shipCargo.Amount[y]++;
                                originCargo.Amount[y]--;
                            }
                        }
                        // Otherwise, do Loading logic.
                        else
                        {
                            // Stop if there are no resources left to load, if its a resource the station uses, or if the ship is full.
                            if ( originCargo.Amount[y] <= 0 || originCargo.PerSecond[y] < 0 || shipCargo.Amount[y] >= shipCargo.Capacity[y] )
                                continue;

                            // Transfer a single resource per second.
                            originCargo.Amount[y]--;
                            shipCargo.Amount[y]++;
                            isFinished = false;
                        }
                    }

                    // Save the resources.
                    originStation.SetCivilianCargoExt( originCargo );
                    cargoShip.SetCivilianCargoExt( shipCargo );

                    // If load timer hit 0, or we're finished stop loading.
                    if ( shipStatus.LoadTimer <= 0 || isFinished )
                    {
                        shipStatus.LoadTimer = 0;
                        shipStatus.Status = CivilianShipStatus.Enroute;
                        cargoShip.SetCivilianStatusExt( shipStatus );
                    }
                }
                // Handle resource unloading.
                else if ( shipStatus.Status == CivilianShipStatus.Unloading )
                {
                    // Load the cargo ship's cargo.
                    CivilianCargo shipCargo = cargoShip.GetCivilianCargoExt();

                    // Load the destination station and its cargo.
                    GameEntity_Squad destinationStation = World_AIW2.Instance.GetEntityByID_Squad( shipStatus.Destination );
                    // If the station has died, free the cargo ship.
                    if ( destinationStation == null )
                    {
                        shipStatus.Status = CivilianShipStatus.Idle;
                        cargoShip.SetCivilianStatusExt( shipStatus );
                        continue;
                    }
                    CivilianCargo destinationCargo = destinationStation.GetCivilianCargoExt();

                    // Send the resources, if the ship has any left.
                    // Check for completion as well here.
                    bool isFinished = true;
                    for ( int y = 0; y < (int)CivilianResource.Length; y++ )
                    {
                        // If the station is producing this resource, and has over 50% of it, give it to the ship, if it has room.
                        if ( destinationCargo.PerSecond[y] > 0 && destinationCargo.Amount[y] > destinationCargo.Capacity[y] / 2 && shipCargo.Amount[y] < shipCargo.Capacity[y] )
                        {
                            destinationCargo.Amount[y]--;
                            shipCargo.Amount[y]++;
                        }
                        else
                        {
                            // Otherwise, do ship unloading logic.
                            // If empty, do nothing.
                            if ( shipCargo.Amount[y] <= 0 )
                                continue;

                            // If station is full, do nothing.
                            if ( destinationCargo.Amount[y] >= destinationCargo.Capacity[y] )
                                continue;

                            // Transfer a single resource per second.
                            shipCargo.Amount[y]--;
                            destinationCargo.Amount[y]++;
                            isFinished = false;
                        }
                    }

                    // Save the resources.
                    destinationStation.SetCivilianCargoExt( destinationCargo );
                    cargoShip.SetCivilianCargoExt( shipCargo );

                    // If ship finished, have it go back to being Idle.
                    if ( isFinished )
                    {
                        shipStatus.Status = CivilianShipStatus.Idle;
                        cargoShip.SetCivilianStatusExt( shipStatus );
                    }
                }
                // Handle resource unloading to stations.
                else if ( shipStatus.Status == CivilianShipStatus.Building )
                {
                    // Load the cargo ship's cargo.
                    CivilianCargo shipCargo = cargoShip.GetCivilianCargoExt();

                    // Load the destination station and its cargo.
                    GameEntity_Squad destinationStation = World_AIW2.Instance.GetEntityByID_Squad( shipStatus.Destination );
                    // If the station has died, free the cargo ship.
                    if ( destinationStation == null )
                    {
                        shipStatus.Status = CivilianShipStatus.Idle;
                        cargoShip.SetCivilianStatusExt( shipStatus );
                        continue;
                    }
                    CivilianMilitia destinationMilitia = destinationStation.GetCivilianMilitiaExt();

                    // Send the resources, if the ship has any left.
                    // Check for completion as well here.
                    bool isFinished = true;
                    for ( int y = 0; y < (int)CivilianResource.Length; y++ )
                    {
                        // If empty, do nothing.
                        if ( shipCargo.Amount[y] <= 0 )
                            continue;

                        // Stop if at capacity.
                        if ( destinationMilitia.ProcessedResources[y] >= destinationMilitia.MaximumCapacity[y] )
                            continue;

                        // Transfer a single resource per second.
                        shipCargo.Amount[y]--;
                        destinationMilitia.ProcessedResources[y]++;
                        isFinished = false;
                    }

                    // Save the resources.
                    destinationStation.SetCivilianMilitiaExt( destinationMilitia );
                    cargoShip.SetCivilianCargoExt( shipCargo );

                    // If ship finished, have it go back to being Idle.
                    if ( isFinished )
                    {
                        shipStatus.Status = CivilianShipStatus.Idle;

                    }
                }
            }
        }

        // Handle station requests.
        public void DoTradeRequests( Faction faction, Faction playerFaction, CivilianFaction factionData, ArcenSimContext Context )
        {
            // Get a list of free CargoShips.
            List<int> freeShipIndexes = new List<int>();
            for ( int x = 0; x < factionData.CargoShips.Count; x++ )
            {
                GameEntity_Squad cargoShip = World_AIW2.Instance.GetEntityByID_Squad( factionData.CargoShips[x] );
                if ( cargoShip == null )
                {
                    factionData.CargoShips.RemoveAt( x );
                    x--;
                    continue;
                }
                if ( cargoShip.GetCivilianStatusExt().Status == CivilianShipStatus.Idle )
                    freeShipIndexes.Add( x );
            }

            #region Planet Level Trading
            // See if any militia stations don't have a trade in progress.
            for ( int x = 0; x < factionData.MilitiaLeaders.Count; x++ )
            {
                GameEntity_Squad militia = World_AIW2.Instance.GetEntityByID_Squad( factionData.MilitiaLeaders[x] );
                if ( militia == null )
                {
                    factionData.MilitiaLeaders.RemoveAt( x );
                    x--;
                    continue;
                }
                if ( militia.GetCivilianMilitiaExt().Status != CivilianMilitiaStatus.Defending && militia.GetCivilianMilitiaExt().Status != CivilianMilitiaStatus.Patrolling )
                    continue;
                // See if we already have cargo ships enroute.
                int cargoEnroute = 0;
                for ( int y = 0; y < factionData.CargoShips.Count; y++ )
                {
                    GameEntity_Squad cargoShip = World_AIW2.Instance.GetEntityByID_Squad( factionData.CargoShips[y] );
                    if ( cargoShip == null )
                    {
                        factionData.CargoShips.RemoveAt( y );
                        y--;
                        continue;
                    }
                    if ( cargoShip.GetCivilianStatusExt().Status == CivilianShipStatus.Idle )
                        continue;
                    if ( cargoShip.GetCivilianStatusExt().Destination == militia.PrimaryKeyID )
                    {
                        cargoEnroute++;
                        if ( cargoEnroute > 0 )
                            break;
                    }
                }

                if ( cargoEnroute > 0 )
                    continue;

                // If no free cargo ships, increment build counter and stop.
                if ( freeShipIndexes.Count == 0 )
                {
                    factionData.BuildCounter += factionData.MilitiaLeaders.Count;
                    continue;
                }

                // Find a trade station either on this planet or adjacent. No further.
                int hopLimit = 0;
                GameEntity_Squad foundStation = null;
                for ( int y = 0; y < factionData.TradeStations.Count && hopLimit <= 1; y++ )
                {
                    GameEntity_Squad tradeStation = World_AIW2.Instance.GetEntityByID_Squad( factionData.TradeStations[y] );
                    if ( tradeStation == null )
                        continue;

                    if ( militia.Planet.GetHopsTo( tradeStation.Planet ) <= hopLimit )
                    {
                        // Found station.
                        foundStation = tradeStation;
                        break;
                    }
                }

                if ( foundStation == null )
                    continue;

                // Find a cargo ship within 2 hops.
                hopLimit = 2;
                GameEntity_Squad foundCargoShip = null;
                for ( int y = 0; y < factionData.CargoShips.Count; y++ )
                {
                    GameEntity_Squad tempShip = World_AIW2.Instance.GetEntityByID_Squad( factionData.CargoShips[y] );
                    if ( tempShip == null )
                        continue;
                    if ( tempShip.GetCivilianStatusExt().Status == CivilianShipStatus.Idle )
                    {
                        foundCargoShip = tempShip;
                        break;
                    }
                }

                // If we have both a ship and a station, send the request.
                if ( foundStation != null && foundCargoShip != null )
                {
                    // Update our cargo ship with its new mission.
                    CivilianStatus cargoShipStatus = foundCargoShip.GetCivilianStatusExt();
                    cargoShipStatus.Origin = foundStation.PrimaryKeyID;
                    cargoShipStatus.Destination = militia.PrimaryKeyID;
                    cargoShipStatus.Status = CivilianShipStatus.Pathing;
                    // Save its updated status.
                    foundCargoShip.SetCivilianStatusExt( cargoShipStatus );
                    // Remove the completed entities from processing.
                    freeShipIndexes.Remove( foundCargoShip.PrimaryKeyID );
                }
            }
            #endregion

            #region Trade Station Imports and Exports

            // Skip if there aren't at least two stations.
            if ( factionData.TradeStations.Count < 2 )
                return;

            // Handle planet level first.

            // Get a list of TradeRequests to be later sorted.
            List<TradeRequest> tradeRequests = new List<TradeRequest>();

            // Populate our TradeRequests list.
            for ( int x = 0; x < factionData.TradeStations.Count; x++ )
            {
                GameEntity_Squad requester = World_AIW2.Instance.GetEntityByID_Squad( factionData.TradeStations[x] );
                if ( requester == null )
                {
                    // Remove invalid ResourcePoints.
                    factionData.TradeStations.RemoveAt( x );
                    x--;
                    continue;
                }
                CivilianCargo requesterCargo = requester.GetCivilianCargoExt();
                if ( requesterCargo == null )
                    continue;

                // Check each type of cargo seperately.
                for ( int y = 0; y < requesterCargo.PerSecond.Length; y++ )
                {
                    // Skip if we don't accept it.
                    if ( requesterCargo.Capacity[y] <= 0 )
                        continue;

                    int incoming = 0;
                    // Lower urgency for each ship inbound to pickup.
                    for ( int z = 0; z < factionData.CargoShips.Count; z++ )
                    {
                        GameEntity_Squad cargoShip = World_AIW2.Instance.GetEntityByID_Squad( factionData.CargoShips[z] );
                        if ( cargoShip == null )
                            continue;
                        CivilianStatus cargoStatus = cargoShip.GetCivilianStatusExt();
                        if ( cargoShip.GetCivilianStatusExt().Status == CivilianShipStatus.Pathing
                          && cargoStatus.Origin == factionData.TradeStations[x] )
                            incoming++;
                    }

                    if ( incoming < 2 )
                        // Resources we generate.
                        if ( requesterCargo.PerSecond[y] > 0 )
                        {
                            // Generates urgency based on how close to full capacity we are.
                            int urgency = (int)Math.Ceiling( (double)(1 - ((requesterCargo.Capacity[y] - requesterCargo.Amount[y]) / requesterCargo.Capacity[y])) * 10 );
                            if ( urgency > 0 )
                            {
                                tradeRequests.Add( new TradeRequest( (CivilianResource)y, urgency, true, requester ) );
                            }
                        }
                        // Resources we use.
                        /*else if ( requesterCargo.PerSecond[y] < 0 )
                        {
                            // Generates urgency based on how close to empty we are.
                            int urgency = (int)Math.Ceiling( (double)((requesterCargo.Capacity[y] - requesterCargo.Amount[y]) / requesterCargo.Capacity[y]) ) * 8;
                            if ( urgency > 0 )
                            {
                                // Lower urgency for each ship inbound to deposit.
                                for ( int z = 0; z < factionData.CargoShips.Count; z++ )
                                {
                                    GameEntity_Squad cargoShip = World_AIW2.Instance.GetEntityByID_Squad( factionData.CargoShips[z] );
                                    if ( cargoShip == null )
                                        continue;
                                    CivilianStatus cargoStatus = cargoShip.GetCivilianStatusExt();
                                    if ( cargoShip.GetCivilianStatusExt().Status == CivilianShipStatus.Enroute
                                      && cargoStatus.Destination == factionData.TradeStations[x] )
                                        urgency--;
                                }
                                // If urgency is still above 0, create a new trade request.
                                if ( urgency > 0 )
                                    tradeRequests.Add( new TradeRequest( (CivilianResource)y, urgency, false, requester ) );
                            }
                        }*/
                        // Resource we store. Simply put out a super tiny order to import/export based on current stores.
                        else
                        {
                            if ( requesterCargo.Amount[y] > requesterCargo.Capacity[y] * 0.5 )
                                tradeRequests.Add( new TradeRequest( (CivilianResource)y, 2, true, requester ) );
                            else
                                tradeRequests.Add( new TradeRequest( (CivilianResource)y, 2, false, requester ) );
                        }
                }
            }

            // If there are no free CargoShips, simply add all Urgency values to the Grand Station.
            if ( freeShipIndexes.Count == 0 )
            {
                factionData.BuildCounter += tradeRequests.Count;
                return;
            }

            // Sort our list.
            tradeRequests.Sort();

            // Initially limit the number of hops to search through, to try and find closer matches to start with.
            // While we have free ships left, assign our requests away.
            int numOfHops = 0;
            while ( numOfHops <= 5 )
            {
                for ( int x = 0; x < tradeRequests.Count; x++ )
                    // If processed, remove.
                    if ( tradeRequests[x].Processed == true )
                        tradeRequests.RemoveAt( x );
                for ( int x = 0; x < tradeRequests.Count && freeShipIndexes.Count > 0; x++ )
                {
                    GameEntity_Squad requestingEntity = tradeRequests[x].Station;
                    if ( requestingEntity == null )
                    {
                        tradeRequests.RemoveAt( x );
                        x--;
                        continue;
                    }
                    // Get a free cargo ship within our hop limit.
                    GameEntity_Squad foundCargoShip = null;
                    int foundIndex = -1;
                    for ( int y = 0; y < freeShipIndexes.Count; y++ )
                    {
                        GameEntity_Squad cargoShip = World_AIW2.Instance.GetEntityByID_Squad( factionData.CargoShips[freeShipIndexes[y]] );
                        if ( cargoShip == null )
                        {
                            freeShipIndexes.RemoveAt( y );
                            y--;
                            continue;
                        }
                        // If few enough hops away for this attempt, assign.
                        if ( cargoShip.Planet.GetHopsTo( requestingEntity.Planet ) <= numOfHops )
                        {
                            foundCargoShip = cargoShip;
                            foundIndex = y;
                            break;
                        }
                    }
                    if ( foundCargoShip == null )
                        continue;
                    // If the cargo ship over 90% of the resource already on it, skip the origin station search, and just have it start heading right towards our requesting station.
                    if ( !tradeRequests[x].IsExport && foundCargoShip.GetCivilianCargoExt().Amount[(int)tradeRequests[x].Requested] > foundCargoShip.GetCivilianCargoExt().Capacity[(int)tradeRequests[x].Requested] * .9 )
                    {
                        // Update our cargo ship with its new mission.
                        CivilianStatus cargoShipStatus = foundCargoShip.GetCivilianStatusExt();
                        cargoShipStatus.Origin = -1;    // No origin station required.
                        cargoShipStatus.Destination = requestingEntity.PrimaryKeyID;
                        cargoShipStatus.Status = CivilianShipStatus.Enroute;
                        // Save its updated status.
                        foundCargoShip.SetCivilianStatusExt( cargoShipStatus );
                        // Remove the completed entities from processing.
                        tradeRequests[x].Processed = true;
                        freeShipIndexes.RemoveAt( foundIndex );
                    }
                    // Find a trade request of the same resource type and opposing Import/Export status thats within our hop limit.
                    GameEntity_Squad otherStation = null;
                    TradeRequest otherRequest = null;
                    for ( int z = 0; z < tradeRequests.Count; z++ )
                    {
                        // Skip if same.
                        if ( x == z )
                            continue;

                        if ( tradeRequests[z].Requested == tradeRequests[x].Requested
                          && tradeRequests[z].IsExport != tradeRequests[x].IsExport
                          && tradeRequests[z].Station.Planet.GetHopsTo( tradeRequests[x].Station.Planet ) <= numOfHops )
                        {
                            otherStation = tradeRequests[z].Station;
                            otherRequest = tradeRequests[z];
                            break;
                        }
                    }
                    if ( otherStation != null )
                    {
                        // Assign our ship to our new trade route, and remove both requests and the ship from our lists.
                        CivilianStatus cargoShipStatus = foundCargoShip.GetCivilianStatusExt();
                        // Make sure the Origin is the Exporter and the Destination is the Importer.
                        if ( tradeRequests[x].IsExport )
                        {
                            cargoShipStatus.Origin = requestingEntity.PrimaryKeyID;
                            cargoShipStatus.Destination = otherStation.PrimaryKeyID;
                        }
                        else
                        {
                            cargoShipStatus.Origin = otherStation.PrimaryKeyID;
                            cargoShipStatus.Destination = requestingEntity.PrimaryKeyID;
                        }
                        cargoShipStatus.Status = CivilianShipStatus.Pathing;
                        // Save its updated status.
                        foundCargoShip.SetCivilianStatusExt( cargoShipStatus );
                        // Remove the completed entities from processing.
                        tradeRequests[x].Processed = true;
                        if ( otherRequest != null )
                            otherRequest.Processed = true;
                        freeShipIndexes.Remove( factionData.CargoShips.IndexOf( foundCargoShip.PrimaryKeyID ) );
                    }
                }
                numOfHops++;
            }
            // If we've finished due to not having enough trade ships, request more cargo ships.
            if ( tradeRequests.Count > 0 && freeShipIndexes.Count == 0 )
                for ( int x = 0; x < tradeRequests.Count; x++ )
                    if ( !tradeRequests[x].Processed )
                        factionData.BuildCounter += 1;

            #endregion
        }

        // Handle assigning militia to our ThreatReports.
        public void DoMilitiaAssignment( Faction faction, Faction playerFaction, CivilianFaction factionData, ArcenSimContext Context )
        {
            // Skip if no threat.
            if ( factionData.ThreatReports == null || factionData.ThreatReports.Count == 0 )
                return;

            // Get a list of free militia leaders.
            List<GameEntity_Squad> freeMilitia = new List<GameEntity_Squad>();

            // Find any free militia leaders and add them to our list.
            for ( int x = 0; x < factionData.MilitiaLeaders.Count; x++ )
            {
                GameEntity_Squad militiaLeader = World_AIW2.Instance.GetEntityByID_Squad( factionData.MilitiaLeaders[x] );
                if ( militiaLeader == null )
                    continue;

                CivilianMilitia militiaStatus = militiaLeader.GetCivilianMilitiaExt();
                if ( militiaStatus == null )
                    continue;

                if ( militiaStatus.Status == CivilianMilitiaStatus.Idle )
                    freeMilitia.Add( militiaLeader );
            }

            // Deal with militia requests.
            for ( int x = 0; x < factionData.ThreatReports.Count; x++ )
            {
                // If not our planet, or we already have enough militia to counteract threat, skip.
                if ( factionData.ThreatReports[x].Planet.GetControllingFaction() != playerFaction
                  || factionData.ThreatReports[x].GetThreat().Actual <= 0 )
                    continue;

                // If we ran out of free militia, update our request.
                if ( freeMilitia.Count == 0 )
                {
                    factionData.MilitiaCounter += 1;
                    continue;
                }

                // See if any wormholes to hostile planets are still unassigned.
                GameEntity_Other foundWormhole = null;
                factionData.ThreatReports[x].Planet.DoForLinkedNeighbors( delegate ( Planet otherPlanet )
                 {
                     // Make sure its a non-player owned planet.
                     if ( otherPlanet.GetControllingFaction().Type == FactionType.Player )
                         return DelReturn.Continue;

                     // Get its wormhole.
                     GameEntity_Other wormhole = factionData.ThreatReports[x].Planet.GetWormholeTo( otherPlanet );
                     if ( wormhole == null )
                         return DelReturn.Continue;

                     // If its not been claimed by another militia, claim it.
                     if ( (from o in factionData.MilitiaLeaders where World_AIW2.Instance.GetEntityByID_Squad( o ).GetCivilianMilitiaExt().EntityFocus == wormhole.PrimaryKeyID select o).Count() == 0 )
                     {
                         foundWormhole = wormhole;
                         return DelReturn.Break;
                     }
                     return DelReturn.Continue;
                 } );


                // If no free wormhole, try to find a free mine.
                GameEntity_Squad foundMine = null;
                factionData.ThreatReports[x].Planet.DoForEntities( EntityRollupType.MetalProducers, delegate ( GameEntity_Squad mineEntity )
                {
                    if ( mineEntity.TypeData.GetHasTag( "MetalGenerator" )
                    && (from o in factionData.MilitiaLeaders where World_AIW2.Instance.GetEntityByID_Squad( o ).GetCivilianMilitiaExt().EntityFocus == mineEntity.PrimaryKeyID select o).Count() == 0 )
                    {
                        foundMine = mineEntity;
                        return DelReturn.Break;
                    }

                    return DelReturn.Continue;
                } );

                // Stop if nothing is free.
                if ( foundWormhole == null && foundMine == null )
                    continue;

                // Find the closest militia ship. Default to first in the list.
                GameEntity_Squad militia = freeMilitia[0];
                // If there is at least one more ship, find the closest to our planet, and pick that one.
                if ( freeMilitia.Count > 1 )
                {
                    for ( int y = 1; y < freeMilitia.Count; y++ )
                    {
                        if ( freeMilitia[y].Planet.GetHopsTo( factionData.ThreatReports[x].Planet ) < militia.Planet.GetHopsTo( factionData.ThreatReports[x].Planet ) )
                            militia = freeMilitia[y];
                    }
                }
                // Remove our found militia from our list.
                freeMilitia.Remove( militia );

                // Update the militia's status.
                CivilianMilitia militiaStatus = militia.GetCivilianMilitiaExt();
                militiaStatus.PlanetFocus = factionData.ThreatReports[x].Planet.PlanetIndex;

                // Assign our mine or wormhole.
                if ( foundWormhole != null )
                {
                    militiaStatus.EntityFocus = foundWormhole.PrimaryKeyID;
                    militiaStatus.Status = CivilianMilitiaStatus.PathingForWormhole;
                }
                else
                {
                    militiaStatus.EntityFocus = foundMine.PrimaryKeyID;
                    militiaStatus.Status = CivilianMilitiaStatus.PathingForMine;
                }

                // Save its status.
                militia.SetCivilianMilitiaExt( militiaStatus );
            }
        }

        // Handle militia deployment and unit building.
        public void DoMilitiaDeployment( Faction faction, Faction playerFaction, CivilianFaction factionData, ArcenSimContext Context )
        {
            // Handle once for each militia leader.
            List<int> toRemove = new List<int>();
            List<int> toAdd = new List<int>();
            for ( int x = 0; x < factionData.MilitiaLeaders.Count; x++ )
            {
                // Load its ship and status.
                GameEntity_Squad militiaShip = World_AIW2.Instance.GetEntityByID_Squad( factionData.MilitiaLeaders[x] );
                if ( militiaShip == null )
                    continue;
                CivilianMilitia militiaStatus = militiaShip.GetCivilianMilitiaExt();
                // Load its goal.
                GameEntity_Squad goalStation = null;
                // Get its planet.
                Planet planet = World_AIW2.Instance.GetPlanetByIndex( militiaStatus.PlanetFocus );
                // If planet not found, idle the militia ship.
                if ( planet == null )
                {
                    militiaStatus.Status = CivilianMilitiaStatus.Idle;
                    militiaShip.SetCivilianMilitiaExt( militiaStatus );
                    continue;
                }
                // Skip if not at planet yet.
                if ( militiaShip.Planet.PlanetIndex != militiaStatus.PlanetFocus )
                    continue;
                // Get its goal's station.
                planet.DoForEntities( delegate ( GameEntity_Squad entity )
                 {
                     // If we find its index in our records, thats our goal station.
                     if ( factionData.TradeStations.Contains( entity.PrimaryKeyID ) )
                     {
                         goalStation = entity;
                         return DelReturn.Break;
                     }

                     return DelReturn.Continue;
                 } );
                // If goal station not found, idle the militia ship.
                if ( goalStation == null )
                {
                    militiaStatus.Status = CivilianMilitiaStatus.Idle;
                    militiaShip.SetCivilianMilitiaExt( militiaStatus );
                    continue;
                }

                // If pathing, check for arrival.
                if ( militiaStatus.Status == CivilianMilitiaStatus.PathingForMine )
                {
                    // If nearby, advance status.
                    if ( militiaShip.GetDistanceTo_ExpensiveAccurate( goalStation.WorldLocation, true, true ) < 500 )
                    {
                        militiaStatus.Status = CivilianMilitiaStatus.EnrouteMine;
                    }
                }
                else if ( militiaStatus.Status == CivilianMilitiaStatus.PathingForWormhole )
                {
                    // If nearby, advance status.
                    if ( militiaShip.GetDistanceTo_ExpensiveAccurate( goalStation.WorldLocation, true, true ) < 500 )
                    {
                        militiaStatus.Status = CivilianMilitiaStatus.EnrouteWormhole;
                    }
                }
                // If enroute, check for sweet spot.
                if ( militiaStatus.Status == CivilianMilitiaStatus.EnrouteWormhole )
                {
                    int stationDist = militiaShip.GetDistanceTo_ExpensiveAccurate( goalStation.WorldLocation, true, true );
                    int wormDist = militiaShip.GetDistanceTo_ExpensiveAccurate( militiaStatus.getWormhole().WorldLocation, true, true );
                    int range = 8000;
                    if ( stationDist > range * 0.2 &&
                        (stationDist > range * 0.6 || wormDist < range) )
                    {
                        // Prepare its old id to be removed.
                        toRemove.Add( militiaShip.PrimaryKeyID );

                        // Optimal distance. Transform the ship and update its status.
                        militiaStatus.Status = CivilianMilitiaStatus.Defending;

                        // Load its station data.
                        GameEntityTypeData outpostData = GameEntityTypeDataTable.Instance.GetRandomRowWithTag( Context, "MilitiaOutpost" );

                        // Transform it.
                        GameEntity_Squad newMilitiaShip = militiaShip.TransformInto( Context, outpostData, 1 );

                        // Move the information to our new ship.
                        newMilitiaShip.SetCivilianMilitiaExt( militiaStatus );

                        // Create a new fleet with our capital ship as the centerpiece.
                        Fleet newFleet = Fleet.Create( FleetCategory.NPC, faction, newMilitiaShip.PlanetFaction, newMilitiaShip );

                        // Prepare its new id to be added.
                        toAdd.Add( newMilitiaShip.PrimaryKeyID );
                    }
                }
                // If enroute, check for sweet spot.
                else if ( militiaStatus.Status == CivilianMilitiaStatus.EnrouteMine )
                {
                    int mineDist = militiaShip.GetDistanceTo_ExpensiveAccurate( militiaStatus.getMine().WorldLocation, true, true );
                    int range = 1000;
                    if ( mineDist < range )
                    {
                        // Prepare its old id to be removed.
                        toRemove.Add( militiaShip.PrimaryKeyID );

                        // Converting to a Barracks, upgrade the fleet status to a mobile patrol.
                        militiaStatus.Status = CivilianMilitiaStatus.Patrolling;

                        // Load its station data.
                        GameEntityTypeData outpostData = GameEntityTypeDataTable.Instance.GetRandomRowWithTag( Context, "MilitiaPatrolPost" );

                        // Transform it.
                        GameEntity_Squad newMilitiaShip = militiaShip.TransformInto( Context, outpostData, 1 );

                        // Make sure its not overlapping.
                        newMilitiaShip.SetWorldLocation( newMilitiaShip.Planet.GetSafePlacementPoint( Context, outpostData, newMilitiaShip.WorldLocation, 0, 1000 ) );

                        // Move the information to our new ship.
                        newMilitiaShip.SetCivilianMilitiaExt( militiaStatus );

                        // Create a new fleet with our capital ship as the centerpiece
                        Fleet newFleet = Fleet.Create( FleetCategory.NPC, faction, newMilitiaShip.PlanetFaction, newMilitiaShip );

                        // Prepare its new id to be added.
                        toAdd.Add( newMilitiaShip.PrimaryKeyID );
                    }
                }
                else if ( militiaStatus.Status == CivilianMilitiaStatus.Defending ) // If defending, do turret placement.
                {
                    // For each type of unit, get turret count.
                    for ( int y = 0; y < (int)CivilianResource.Length; y++ )
                    {
                        GameEntityTypeData typeData = GameEntityTypeDataTable.Instance.GetRowByName( ((CivilianMilitiaTurretType)y).ToString(), false, null );
                        int count = 0;
                        try
                        {
                            List<SquadWrapper> squads = militiaShip.FleetMembership.Fleet.GetButDoNotAddMembershipGroupBasedOnSquadType_AssumeNoDuplicates( typeData ).Entities;
                            for ( int z = 0; z < squads.Count; z++ )
                                count += 1 + squads[z].GetSquad().ExtraStackedSquadsInThis;
                        }
                        catch ( Exception )
                        {
                            count = 0;
                        }
                        if ( count >= factionData.GetCap() )
                            continue;

                        // Get cargo and ship cost.
                        int cost = factionData.GetResourceCost( count );
                        if ( militiaStatus.ProcessedResources[y] >= cost )
                        {
                            // Remove cost.
                            militiaStatus.ProcessedResources[y] -= cost;
                            // Spawn turret.
                            // Get a focal point directed towards the wormhole.
                            ArcenPoint basePoint = militiaShip.WorldLocation.GetPointAtAngleAndDistance( militiaShip.WorldLocation.GetAngleToDegrees( militiaStatus.getWormhole().WorldLocation ), Math.Min( 5000, goalStation.GetDistanceTo_ExpensiveAccurate( militiaStatus.getWormhole().WorldLocation, true, true ) / 2 ) );
                            // Get a point around it, as close as possible.
                            ArcenPoint spawnPoint = basePoint.GetRandomPointWithinDistance( Context.RandomToUse, Math.Min( 500, goalStation.GetDistanceTo_ExpensiveAccurate( militiaStatus.getWormhole().WorldLocation, true, true ) / 4 ), Math.Min( 2500, goalStation.GetDistanceTo_ExpensiveAccurate( militiaStatus.getWormhole().WorldLocation, true, true ) / 2 ) );

                            // Load turret data. Default for metal: Pike
                            GameEntityTypeData entityData = GameEntityTypeDataTable.Instance.GetRowByName( ((CivilianMilitiaTurretType)y).ToString(), false, null );

                            // Get the planet faction to spawn it in as.
                            PlanetFaction pFaction = militiaShip.Planet.GetPlanetFactionForFaction( faction );

                            // Spawn in the ship.
                            GameEntity_Squad entity = GameEntity_Squad.CreateNew( pFaction, entityData, entityData.MarkFor( pFaction ), pFaction.FleetUsedAtPlanet, 0, spawnPoint, Context );

                            // Add the turret to our militia's fleet.
                            militiaShip.FleetMembership.Fleet.AddSquadToMembership_AssumeNoDuplicates( entity );
                        }
                    }
                    // Save our militia's status.
                    militiaShip.SetCivilianMilitiaExt( militiaStatus );
                }
                else if ( militiaStatus.Status == CivilianMilitiaStatus.Patrolling ) // If patrolling, do unit spawning.
                {
                    // For each type of unit, get ship count.
                    for ( int y = 0; y < (int)CivilianResource.Length; y++ )
                    {
                        GameEntityTypeData typeData = GameEntityTypeDataTable.Instance.GetRowByName( ((CivilianMilitiaShipType)y).ToString(), false, null );
                        int count = 0;
                        try
                        {
                            List<SquadWrapper> squads = militiaShip.FleetMembership.Fleet.GetButDoNotAddMembershipGroupBasedOnSquadType_AssumeNoDuplicates( typeData ).Entities;
                            for ( int z = 0; z < squads.Count; z++ )
                                count += 1 + squads[z].GetSquad().ExtraStackedSquadsInThis;
                        }
                        catch ( Exception )
                        {
                            count = 0;
                        }
                        if ( count >= factionData.GetCap() )
                            continue;

                        // Get cargo and ship cost.
                        int cost = factionData.GetResourceCost( count );
                        if ( militiaStatus.ProcessedResources[y] >= cost )
                        {
                            // Remove cost.
                            militiaStatus.ProcessedResources[y] -= cost;
                            // Spawn ship.

                            // Load ship data for metal.
                            GameEntityTypeData entityData = GameEntityTypeDataTable.Instance.GetRowByName( ((CivilianMilitiaShipType)y).ToString(), false, null ); ;

                            // Get the planet faction to spawn it in as.
                            PlanetFaction pFaction = militiaShip.Planet.GetPlanetFactionForFaction( faction );

                            // Spawn in the ship.
                            GameEntity_Squad entity = GameEntity_Squad.CreateNew( pFaction, entityData, entityData.MarkFor( pFaction ), pFaction.FleetUsedAtPlanet, 0, militiaShip.WorldLocation, Context );

                            // Add the ship to our militia's fleet.
                            militiaShip.FleetMembership.Fleet.AddSquadToMembership_AssumeNoDuplicates( entity );

                            // Have the ship attack hostiles on the planet.
                            entity.Orders.SetBehavior( EntityBehaviorType.Attacker_Full, faction.FactionIndex );

                            // Make the entity only stack with its own fleet.
                            entity.MinorFactionStackingID = militiaShip.PrimaryKeyID;
                        }
                    }
                    // Save our militia's status.
                    militiaShip.SetCivilianMilitiaExt( militiaStatus );
                }
            }
            for ( int x = 0; x < toRemove.Count; x++ )
            {
                factionData.MilitiaLeaders.Remove( toRemove[x] );
                factionData.MilitiaLeaders.Add( toAdd[x] );
            }
        }

        // The following function is called once every second. Consider this our 'main' function of sorts, all of our logic is based on this bad boy calling all our pieces every second.
        public override void DoPerSecondLogic_Stage3Main_OnMainThread( Faction faction, ArcenSimContext Context )
        {
            // Update faction relations. Generally a good idea to have this in your DoPerSecondLogic function since other factions can also change their allegiances.
            allyThisFactionToHumans( faction );

            // Load our data.
            // Start with world.
            CivilianWorld worldData = World.Instance.GetCivilianWorldExt();
            if ( worldData == null )
                return;

            // Make sure we have a faction entry in our global data for every player faction in game.
            for ( int i = 0; i < World_AIW2.Instance.Factions.Count; i++ )
            {
                Faction otherFaction = World_AIW2.Instance.Factions[i];
                if ( otherFaction.Type == FactionType.Player && !worldData.Factions.Contains( otherFaction.FactionIndex ) )
                    worldData.Factions.Add( otherFaction.FactionIndex );
            }

            // Next, do logic once for each faction that has a registered industry.
            for ( int x = 0; x < worldData.Factions.Count; x++ )
            {
                // Load in this faction's data.
                (bool valid, Faction playerFaction, CivilianFaction factionData) = worldData.getFactionInfo( x );
                if ( !valid )
                    continue;

                // If not currently active, create the faction's starting station.
                while (World_AIW2.Instance.GetEntityByID_Squad(factionData.GrandStation) == null)
                    CreateGrandStation( faction, playerFaction, factionData, Context );

                // Handle spawning of trade stations.
                CreateTradeStations( faction, playerFaction, factionData, Context );

                // Handle basic resource generation. (Resources with no requirements, ala Goods or Ore.)
                DoResources( faction, playerFaction, factionData, Context );

                // Handle the creation of ships.
                DoShipSpawns( faction, playerFaction, factionData, Context );

                // Check for ship arrival.
                DoShipArrival( faction, playerFaction, factionData, Context );

                // Handle resource transfering.
                DoResourceTransfer( faction, playerFaction, factionData, Context );

                // Handle station requests.
                DoTradeRequests( faction, playerFaction, factionData, Context );

                // Handle assigning militia to our ThreatReports.
                DoMilitiaAssignment( faction, playerFaction, factionData, Context );

                // Handle militia deployment and unit building.
                DoMilitiaDeployment( faction, playerFaction, factionData, Context );

                // Update the mark level of all units every now and than.
                if ( World_AIW2.Instance.GameSecond % 5 == 0 )
                    faction.Entities.DoForEntities( delegate ( GameEntity_Squad squad )
                    {
                        // Mark level is equal to the higher value between Planet Mark, Global AI Mark, or Player Mark for that ship type.
                        int mark = 1;
                        for ( int y = 0; y < World_AIW2.Instance.AIFactions.Count; y++ )
                            mark = Math.Max( mark, World_AIW2.Instance.AIFactions[y].CurrentGeneralMarkLevel );
                        mark = Math.Max( mark, squad.Planet.MarkLevelForAIOnly.Ordinal );
                        mark = Math.Max( mark, playerFaction.GetGlobalMarkLevelForShipLine( squad.TypeData ) );
                        squad.SetCurrentMarkLevelIfHigherThanCurrent( mark, Context );
                        return DelReturn.Continue;
                    } );

                // Save our faction data.
                playerFaction.SetCivilianFactionExt( factionData );
            }

            // Save our world data.
            World.Instance.SetCivilianWorldExt( worldData );
        }

        // Handle movement of cargo ships to their orign and destination points.
        public void DoCargoShipMovement( Faction faction, Faction playerFaction, CivilianFaction factionData, ArcenLongTermIntermittentPlanningContext Context )
        {
            // Loop through each of our cargo ships.
            for ( int x = 0; x < factionData.CargoShips.Count; x++ )
            {
                // Load the ship and its status.
                GameEntity_Squad ship = World_AIW2.Instance.GetEntityByID_Squad( factionData.CargoShips[x] );
                if ( ship == null )
                    continue;
                CivilianStatus shipStatus = ship.GetCivilianStatusExt();
                if ( shipStatus == null )
                    continue;

                // Pathing movement.
                if ( shipStatus.Status == CivilianShipStatus.Pathing )
                {
                    // Ship currently moving towards origin station.
                    GameEntity_Squad originStation = World_AIW2.Instance.GetEntityByID_Squad( shipStatus.Origin );
                    if ( originStation == null )
                        continue;
                    Planet originPlanet = originStation.Planet;

                    // Check if already on planet.
                    if ( ship.Planet.PlanetIndex == originPlanet.PlanetIndex )
                    {
                        // On planet. Begin pathing towards the station.
                        // Tell the game what kind of command we want to do.
                        // Here, we'll be using the self descriptive MoveManyToOnePoint command.
                        // Note: Despite saying Many, it is also used for singular movement commands.
                        GameCommand command = GameCommand.Create( BaseGameCommand.CommandsByCode[BaseGameCommand.Code.MoveManyToOnePoint], GameCommandSource.AnythingElse );

                        // Let the game know where we want to move to. In this case, to our origin station's location.
                        command.RelatedPoints.Add( originStation.WorldLocation );

                        // Have the command apply to our ship.
                        command.RelatedEntityIDs.Add( ship.PrimaryKeyID );

                        // Tell the game to apply our command.
                        Context.QueueCommandForSendingAtEndOfContext( command );
                    }
                    else
                    {
                        // Not on planet yet, prepare wormhole navigation.
                        // Tell the game wehat kind of command we want to do.
                        // Here we'll be using the self descriptive SetWormholePath command.
                        GameCommand command = GameCommand.Create( BaseGameCommand.CommandsByCode[BaseGameCommand.Code.SetWormholePath], GameCommandSource.AnythingElse );

                        // For wormhole pathing, we'll need to get our path from here to our goal.
                        FactionCommonExternalData factionExternal = faction.GetCommonExternal();
                        PlanetPathfinder pathfinder = factionExternal.ConservativePathfinder_LongTerm;
                        List<Planet> path = pathfinder.FindPath( ship.Planet, originPlanet, 0, 0, false );

                        // Set the goal to the next planet in our path.
                        command.RelatedIntegers.Add( path[1].PlanetIndex );

                        // Have the command apply to our ship.
                        command.RelatedEntityIDs.Add( ship.PrimaryKeyID );

                        // Tell the game to apply our command.
                        Context.QueueCommandForSendingAtEndOfContext( command );
                    }
                }
                else if ( shipStatus.Status == CivilianShipStatus.Enroute )
                {
                    // Enroute movement.
                    // ship currently moving towards destination station.
                    GameEntity_Squad destinationStation = World_AIW2.Instance.GetEntityByID_Squad( shipStatus.Destination );
                    if ( destinationStation == null )
                        continue;
                    Planet destinationPlanet = destinationStation.Planet;

                    // Check if already on planet.
                    if ( ship.Planet.PlanetIndex == destinationPlanet.PlanetIndex )
                    {
                        // On planet. Begin pathing towards the station.
                        // Tell the game what kind of command we want to do.
                        // Here, we'll be using the self descriptive MoveManyToOnePoint command.
                        // Note: Despite saying Many, it is also used for singular movement commands.
                        GameCommand command = GameCommand.Create( BaseGameCommand.CommandsByCode[BaseGameCommand.Code.MoveManyToOnePoint], GameCommandSource.AnythingElse );

                        // Let the game know where we want to move to. In this case, to our d station's location.
                        command.RelatedPoints.Add( destinationStation.WorldLocation );

                        // Have the command apply to our ship.
                        command.RelatedEntityIDs.Add( ship.PrimaryKeyID );

                        // Tell the game to apply our command.
                        Context.QueueCommandForSendingAtEndOfContext( command );
                    }
                    else
                    {
                        // Not on planet yet, prepare wormhole navigation.
                        // Tell the game wehat kind of command we want to do.
                        // Here we'll be using the self descriptive SetWormholePath command.
                        GameCommand command = GameCommand.Create( BaseGameCommand.CommandsByCode[BaseGameCommand.Code.SetWormholePath], GameCommandSource.AnythingElse );

                        // For wormhole pathing, we'll need to get our path from here to our goal.
                        FactionCommonExternalData factionExternal = faction.GetCommonExternal();
                        PlanetPathfinder pathfinder = factionExternal.ConservativePathfinder_LongTerm;
                        List<Planet> path = pathfinder.FindPath( ship.Planet, destinationPlanet, 0, 0, false );

                        // Set the goal to the next planet in our path.
                        command.RelatedIntegers.Add( path[1].PlanetIndex );

                        // Have the command apply to our ship.
                        command.RelatedEntityIDs.Add( ship.PrimaryKeyID );

                        // Tell the game to apply our command.
                        Context.QueueCommandForSendingAtEndOfContext( command );
                    }
                }
            }
        }

        // Handle movement of militia capital ships.
        public void DoMilitiaCapitalShipMovement( Faction faction, Faction playerFaction, CivilianFaction factionData, ArcenLongTermIntermittentPlanningContext Context )
        {
            // Loop through each of our militia ships.
            for ( int x = 0; x < factionData.MilitiaLeaders.Count; x++ )
            {
                // Load the ship and its status.
                GameEntity_Squad ship = World_AIW2.Instance.GetEntityByID_Squad( factionData.MilitiaLeaders[x] );
                if ( ship == null )
                    continue;
                CivilianMilitia shipStatus = ship.GetCivilianMilitiaExt();
                if ( shipStatus == null )
                    continue;
                Planet planet = World_AIW2.Instance.GetPlanetByIndex( shipStatus.PlanetFocus );
                if ( planet == null )
                    continue;

                // Pathing movement.
                if ( shipStatus.Status == CivilianMilitiaStatus.PathingForMine || shipStatus.Status == CivilianMilitiaStatus.PathingForWormhole )
                {
                    // Check if already on planet.
                    if ( ship.Planet.PlanetIndex == shipStatus.PlanetFocus )
                    {
                        // On planet. Begin pathing towards the station.
                        GameEntity_Squad goalStation = null;

                        // Find the trade station.
                        planet.DoForEntities( delegate ( GameEntity_Squad entity )
                         {
                             // If we find its index in our records, thats our trade station.
                             if ( factionData.TradeStations.Contains( entity.PrimaryKeyID ) )
                             {
                                 goalStation = entity;
                                 return DelReturn.Break;
                             }

                             return DelReturn.Continue;
                         } );

                        if ( goalStation == null )
                            continue;

                        // Tell the game what kind of command we want to do.
                        // Here, we'll be using the self descriptive MoveManyToOnePoint command.
                        // Note: Despite saying Many, it is also used for singular movement commands.
                        GameCommand command = GameCommand.Create( BaseGameCommand.CommandsByCode[BaseGameCommand.Code.MoveManyToOnePoint], GameCommandSource.AnythingElse );

                        // Let the game know where we want to move to. In this case, to our origin station's location.
                        command.RelatedPoints.Add( goalStation.WorldLocation );

                        // Have the command apply to our ship.
                        command.RelatedEntityIDs.Add( ship.PrimaryKeyID );

                        // Tell the game to apply our command.
                        Context.QueueCommandForSendingAtEndOfContext( command );
                    }
                    else
                    {
                        // Not on planet yet, prepare wormhole navigation.
                        // Tell the game wehat kind of command we want to do.
                        // Here we'll be using the self descriptive SetWormholePath command.
                        GameCommand command = GameCommand.Create( BaseGameCommand.CommandsByCode[BaseGameCommand.Code.SetWormholePath], GameCommandSource.AnythingElse );

                        // For wormhole pathing, we'll need to get our path from here to our goal.
                        FactionCommonExternalData factionExternal = faction.GetCommonExternal();
                        PlanetPathfinder pathfinder = factionExternal.ConservativePathfinder_LongTerm;
                        List<Planet> path = pathfinder.FindPath( ship.Planet, planet, 0, 0, false );

                        // Set the goal to the next planet in our path.
                        command.RelatedIntegers.Add( path[1].PlanetIndex );

                        // Have the command apply to our ship.
                        command.RelatedEntityIDs.Add( ship.PrimaryKeyID );

                        // Tell the game to apply our command.
                        Context.QueueCommandForSendingAtEndOfContext( command );
                    }
                }
                else if ( shipStatus.Status == CivilianMilitiaStatus.EnrouteWormhole )
                {
                    // Enroute movement.
                    // Ship has made it to the planet (and, if detected, the trade station on the planet).
                    // We'll now have it begin moving towards its assigned wormhole.
                    // Distance detection for it is handled in the persecond logic further up, all this handles are movement commands.
                    GameEntity_Other wormhole = shipStatus.getWormhole();
                    if ( wormhole == null )
                    {
                        ArcenDebugging.SingleLineQuickDebug( "Civilian Industries: Failed to find wormhole." );
                        continue;
                    }

                    // Tell the game what kind of command we want to do.
                    // Here, we'll be using the self descriptive MoveManyToOnePoint command.
                    // Note: Despite saying Many, it is also used for singular movement commands.
                    GameCommand command = GameCommand.Create( BaseGameCommand.CommandsByCode[BaseGameCommand.Code.MoveManyToOnePoint], GameCommandSource.AnythingElse );

                    // Let the game know where we want to move to.
                    command.RelatedPoints.Add( ship.WorldLocation.GetPointAtAngleAndDistance( ship.WorldLocation.GetAngleToDegrees( wormhole.WorldLocation ), 5000 ) );

                    // Have the command apply to our ship.
                    command.RelatedEntityIDs.Add( ship.PrimaryKeyID );

                    // Tell the game to apply our command.
                    Context.QueueCommandForSendingAtEndOfContext( command );
                }
                else if ( shipStatus.Status == CivilianMilitiaStatus.EnrouteMine )
                {
                    // Enroute movement.
                    // Ship has made it to the planet (and, if detected, the trade station on the planet).
                    // We'll now have it begin moving towards its assigned mine.
                    // Distance detection for it is handled in the persecond logic further up, all this handles are movement commands.
                    GameEntity_Squad mine = shipStatus.getMine();
                    if ( mine == null )
                    {
                        ArcenDebugging.SingleLineQuickDebug( "Civilian Industries: Failed to find mine." );
                        continue;
                    }

                    // Tell the game what kind of command we want to do.
                    // Here, we'll be using the self descriptive MoveManyToOnePoint command.
                    // Note: Despite saying Many, it is also used for singular movement commands.
                    GameCommand command = GameCommand.Create( BaseGameCommand.CommandsByCode[BaseGameCommand.Code.MoveManyToOnePoint], GameCommandSource.AnythingElse );

                    // Let the game know where we want to move to.
                    command.RelatedPoints.Add( mine.WorldLocation );

                    // Have the command apply to our ship.
                    command.RelatedEntityIDs.Add( ship.PrimaryKeyID );

                    // Tell the game to apply our command.
                    Context.QueueCommandForSendingAtEndOfContext( command );
                }
            }
        }

        // Handle reactive moevement of patrolling ship fleets.
        public void DoMilitiaThreatReaction( Faction faction, Faction playerFaction, CivilianFaction factionData, ArcenLongTermIntermittentPlanningContext Context )
        {
            // If we don't have any threat reports yet (usually due to game load) wait.
            if ( factionData.ThreatReports == null || factionData.ThreatReports.Count == 0 )
                return;

            // Amount of strength ready to raid on each planet.
            // This means that it, and all friendly planets adjacent to it, are safe.
            Dictionary<Planet, int> raidStrength = new Dictionary<Planet, int>();

            // Planets that have been given an order. Used for patrolling logic at the bottom.
            List<Planet> isPatrolling = new List<Planet>();

            // Process all militia forces that are currently patrolling.
            #region Defensive Actions
            for ( int x = 0; x < factionData.MilitiaLeaders.Count; x++ )
            {
                GameEntity_Squad centerpiece = World_AIW2.Instance.GetEntityByID_Squad( factionData.MilitiaLeaders[x] );
                if ( centerpiece == null || centerpiece.GetCivilianMilitiaExt().Status != CivilianMilitiaStatus.Patrolling )
                    continue;

                // Where are we going to send all our units?
                Planet targetPlanet = null;

                // If self or an adjacent friendly planet has hostile units on it that outnumber friendly defenses, including incoming waves, protect it.
                for ( int y = 0; y < factionData.ThreatReports.Count && targetPlanet == null; y++ )
                {
                    ThreatReport report = factionData.ThreatReports[y];

                    if ( report.GetThreat().Actual > 0
                     && report.Planet.GetControllingFaction() == playerFaction
                     && report.Planet.GetHopsTo( centerpiece.Planet ) <= 1 )
                    {
                        targetPlanet = report.Planet;
                    }
                }

                // If we have a target for defensive action, act on it.
                if ( targetPlanet != null )
                {
                    isPatrolling.Add( centerpiece.Planet );
                    centerpiece.FleetMembership.Fleet.DoForEntities( delegate ( GameEntity_Squad entity )
                    {
                        // If we're not on our target yet, path to it.
                        if ( entity.Planet != targetPlanet )
                        {
                            // If we're not on the target planet, return to our centerpiece's planet first, to make sure we don't path through hostile territory.
                            if ( entity.Planet != centerpiece.Planet )
                            {
                                // Get a path for the ship to take, and give them the command.
                                List<Planet> path = faction.FindPath( entity.Planet, centerpiece.Planet, Context );

                                // Create and add all required parts of a wormhole move command.
                                GameCommand command = GameCommand.Create( BaseGameCommand.CommandsByCode[BaseGameCommand.Code.SetWormholePath], GameCommandSource.AnythingElse );
                                command.RelatedEntityIDs.Add( entity.PrimaryKeyID );
                                for ( int y = 0; y < path.Count; y++ )
                                    command.RelatedIntegers.Add( path[y].PlanetIndex );
                                Context.QueueCommandForSendingAtEndOfContext( command );
                            }
                            else
                            {
                                // Get a path for the ship to take, and give them the command.
                                List<Planet> path = faction.FindPath( entity.Planet, targetPlanet, Context );

                                // Create and add all required parts of a wormhole move command.
                                GameCommand command = GameCommand.Create( BaseGameCommand.CommandsByCode[BaseGameCommand.Code.SetWormholePath], GameCommandSource.AnythingElse );
                                command.RelatedEntityIDs.Add( entity.PrimaryKeyID );
                                for ( int y = 0; y < path.Count; y++ )
                                    command.RelatedIntegers.Add( path[y].PlanetIndex );
                                Context.QueueCommandForSendingAtEndOfContext( command );
                            }
                        }

                        return DelReturn.Continue;
                    } );
                }
                else
                {
                    // If we have at least one planet adjacent to us that is hostile and threatening, add our patrol posts to the raiding pool.
                    centerpiece.Planet.DoForLinkedNeighbors( delegate ( Planet adjPlanet )
                    {
                        if ( adjPlanet.GetControllingFaction().GetIsHostileTowards( playerFaction ) )
                        {
                            if ( raidStrength.ContainsKey( centerpiece.Planet ) )
                                raidStrength[centerpiece.Planet] += centerpiece.FleetMembership.Fleet.CalculateEffectiveCurrentFleetStrength();
                            else
                                raidStrength.Add( centerpiece.Planet, centerpiece.FleetMembership.Fleet.CalculateEffectiveCurrentFleetStrength() );
                            return DelReturn.Break;
                        }
                        return DelReturn.Continue;
                    } );
                }
            }
            #endregion

            #region Offensive Actions
            // Figure out the potential strength we would have to attack each planet.
            List<AttackAssessment> attackAssessments = new List<AttackAssessment>();
            if ( raidStrength.Count > 0 && raidStrength.Count > 0 )

                foreach ( KeyValuePair<Planet, int> raidingPlanet in raidStrength )
                {
                    raidingPlanet.Key.DoForLinkedNeighbors( delegate ( Planet adjPlanet )
                     {
                         // If friendly, skip.
                         if ( raidingPlanet.Key.GetControllingFaction().GetIsFriendlyTowards( adjPlanet.GetControllingFaction() ) )
                             return DelReturn.Continue;

                         // If we don't yet have an assessment for the planet, and it has enough threat, add it
                         if ( factionData.GetThreat( adjPlanet ).Hostile > 0 )
                         {
                             AttackAssessment adjAssessment = (from o in attackAssessments where o.Target == adjPlanet select o).FirstOrDefault();
                             if ( adjAssessment == null )
                             {
                                 adjAssessment = new AttackAssessment( adjPlanet, (int)(factionData.GetThreat( adjPlanet ).Actual * 1.5) );
                                 attackAssessments.Add( adjAssessment );
                             }
                             // Add our current fleet strength to the attack budget.
                             adjAssessment.Attackers.Add( raidingPlanet.Key, raidingPlanet.Value );
                         }
                         return DelReturn.Continue;
                     } );
                }

            // Sort by strongest planets first. We want to attempt to take down the strongest planet.
            attackAssessments.Sort();

            // Keep poising to attack as long as the target we're aiming for is weak to us.
            while ( attackAssessments.Count > 0 )
            {
                // If not strong enough, remove.
                if ( attackAssessments[0].AttackPower < attackAssessments[0].StrengthRequired )
                {
                    attackAssessments.RemoveAt( 0 );
                    continue;
                }

                if ( attackAssessments[0].Target.Name.Contains( "aby" ) )
                    ArcenDebugging.SingleLineQuickDebug( attackAssessments[0].Target.Name + " " + attackAssessments[0].AttackPower + "/" + attackAssessments[0].StrengthRequired );

                // Stop the attack if too many ships aren't ready, unless we're already attacking.
                int notReady = 0, ready = 0;
                bool alreadyAttacking = false;

                // See if there are already any player units on the planet.
                // If there are, we should be heading there as soon as possible.
                if ( factionData.GetThreat( attackAssessments[0].Target ).Friendly >= 1000 )
                    alreadyAttacking = true;

                for ( int x = 0; x < factionData.MilitiaLeaders.Count; x++ )
                {
                    // Skip checks if we're already attacking or have already gotten enough strength.
                    if ( alreadyAttacking )
                        break;

                    GameEntity_Squad centerpiece = World_AIW2.Instance.GetEntityByID_Squad( factionData.MilitiaLeaders[x] );
                    if ( centerpiece == null || centerpiece.GetCivilianMilitiaExt().Status != CivilianMilitiaStatus.Patrolling || !attackAssessments[0].Attackers.Keys.Contains( centerpiece.Planet ) )
                        continue;

                    centerpiece.FleetMembership.Fleet.DoForEntities( delegate ( GameEntity_Squad entity )
                     {
                         // Skip centerpiece.
                         if ( centerpiece == entity )
                             return DelReturn.Continue;

                         // Already attacking, stop checking and start raiding.
                         if ( entity.Planet == attackAssessments[0].Target )
                         {
                             alreadyAttacking = true;
                             return DelReturn.Break;
                         }

                         // Get them moving if needed.
                         if ( entity.Planet != centerpiece.Planet )
                         {
                             notReady++;
                             // Get a path for the ship to take, and give them the command.
                             List<Planet> path = faction.FindPath( entity.Planet, centerpiece.Planet, Context );

                             // Create and add all required parts of a wormhole move command.
                             GameCommand command = GameCommand.Create( BaseGameCommand.CommandsByCode[BaseGameCommand.Code.SetWormholePath], GameCommandSource.AnythingElse );
                             command.RelatedEntityIDs.Add( entity.PrimaryKeyID );
                             for ( int y = 0; y < path.Count; y++ )
                                 command.RelatedIntegers.Add( path[y].PlanetIndex );
                             Context.QueueCommandForSendingAtEndOfContext( command );
                         }
                         else if ( centerpiece.Planet.GetWormholeTo( attackAssessments[0].Target ).WorldLocation.GetExtremelyRoughDistanceTo( entity.WorldLocation ) > 5000 )
                         {
                             notReady++;
                             // Create and add all required parts of a move to point command.
                             GameCommand command = GameCommand.Create( BaseGameCommand.CommandsByCode[BaseGameCommand.Code.MoveManyToOnePoint], GameCommandSource.AnythingElse );
                             command.RelatedEntityIDs.Add( entity.PrimaryKeyID );
                             command.RelatedPoints.Add( centerpiece.Planet.GetWormholeTo( attackAssessments[0].Target ).WorldLocation );
                             Context.QueueCommandForSendingAtEndOfContext( command );
                         }
                         else
                             ready++;

                         return DelReturn.Continue;
                     } );
                }

                // If 33% all of our ships are ready, or we're already raiding, its raiding time.
                if ( ready > notReady * 2 || alreadyAttacking )
                {
                    for ( int x = 0; x < factionData.MilitiaLeaders.Count; x++ )
                    {
                        GameEntity_Squad centerpiece = World_AIW2.Instance.GetEntityByID_Squad( factionData.MilitiaLeaders[x] );
                        if ( centerpiece == null || centerpiece.GetCivilianMilitiaExt().Status != CivilianMilitiaStatus.Patrolling || !attackAssessments[0].Attackers.Keys.Contains( centerpiece.Planet ) )
                            continue;

                        centerpiece.FleetMembership.Fleet.DoForEntities( delegate ( GameEntity_Squad entity )
                        {
                            if ( entity.Planet != attackAssessments[0].Target )
                            {
                                // Get a path for the ship to take, and give them the command.
                                List<Planet> path = faction.FindPath( entity.Planet, attackAssessments[0].Target, Context );

                                // Create and add all required parts of a wormhole move command.
                                GameCommand command = GameCommand.Create( BaseGameCommand.CommandsByCode[BaseGameCommand.Code.SetWormholePath], GameCommandSource.AnythingElse );
                                command.RelatedEntityIDs.Add( entity.PrimaryKeyID );
                                for ( int y = 0; y < path.Count; y++ )
                                    command.RelatedIntegers.Add( path[y].PlanetIndex );
                                Context.QueueCommandForSendingAtEndOfContext( command );
                            }
                            return DelReturn.Continue;
                        } );
                    }
                }

                // If any of the planets involved in this attack are in other attacks, remove them from those other attacks.
                foreach ( Planet attackingPlanet in attackAssessments[0].Attackers.Keys )
                {
                    isPatrolling.Add( attackingPlanet );
                    for ( int y = 1; y < attackAssessments.Count; y++ )
                    {
                        if ( attackAssessments[y].Attackers.ContainsKey( attackingPlanet ) )
                            attackAssessments[y].Attackers.Remove( attackingPlanet );
                    }
                }

                attackAssessments.RemoveAt( 0 );
                attackAssessments.Sort();
            }
            #endregion

            #region Patrolling Actions
            // If we don't have an active defensive or offensive target, simple patrol around the most threatened allied planet.
            for ( int x = 0; x < factionData.MilitiaLeaders.Count; x++ )
            {
                GameEntity_Squad centerpiece = World_AIW2.Instance.GetEntityByID_Squad( factionData.MilitiaLeaders[x] );
                if ( centerpiece == null || centerpiece.GetCivilianMilitiaExt().Status != CivilianMilitiaStatus.Patrolling || isPatrolling.Contains( centerpiece.Planet ) )
                    continue;

                // Where are we going to send?
                Planet targetPlanet = null;
                for ( int y = 0; y < factionData.ThreatReports.Count && targetPlanet == null; y++ )
                {
                    ThreatReport report = factionData.ThreatReports[y];
                    if ( report.Planet.GetControllingFaction() == playerFaction
                     && report.Planet.GetHopsTo( centerpiece.Planet ) <= 1 )
                    {
                        targetPlanet = report.Planet;
                        break;
                    }
                }
                centerpiece.FleetMembership.Fleet.DoForEntities( delegate ( GameEntity_Squad entity )
                {
                    // If threat on our current planet, wait.
                    if ( factionData.GetThreat( entity.Planet ).Hostile > 0 )
                        return DelReturn.Continue;

                    // If we're not on our target yet, path to it.
                    if ( entity.Planet != targetPlanet )
                    {
                        // If we're not on the target planet, return to our centerpiece's planet first, to make sure we don't path through hostile territory.
                        if ( entity.Planet != centerpiece.Planet )
                        {
                            // Get a path for the ship to take, and give them the command.
                            List<Planet> path = faction.FindPath( entity.Planet, centerpiece.Planet, Context );

                            // Create and add all required parts of a wormhole move command.
                            GameCommand command = GameCommand.Create( BaseGameCommand.CommandsByCode[BaseGameCommand.Code.SetWormholePath], GameCommandSource.AnythingElse );
                            command.RelatedEntityIDs.Add( entity.PrimaryKeyID );
                            for ( int y = 0; y < path.Count; y++ )
                                command.RelatedIntegers.Add( path[y].PlanetIndex );
                            Context.QueueCommandForSendingAtEndOfContext( command );
                        }
                        else
                        {
                            // Get a path for the ship to take, and give them the command.
                            List<Planet> path = faction.FindPath( entity.Planet, targetPlanet, Context );

                            // Create and add all required parts of a wormhole move command.
                            GameCommand command = GameCommand.Create( BaseGameCommand.CommandsByCode[BaseGameCommand.Code.SetWormholePath], GameCommandSource.AnythingElse );
                            command.RelatedEntityIDs.Add( entity.PrimaryKeyID );
                            for ( int y = 0; y < path.Count; y++ )
                                command.RelatedIntegers.Add( path[y].PlanetIndex );
                            Context.QueueCommandForSendingAtEndOfContext( command );
                        }
                    }

                    return DelReturn.Continue;
                } );
            }
            #endregion
        }

        // Called once every X seconds, as defined at the start of our faction class.
        // Do NOT directly change anything from this function. Doing so may cause desyncs in multiplayer.
        // What you can do from here is queue up game commands for units, and send them to be done via QueueCommandForSendingAtEndOfContext.
        public override void DoLongRangePlanning_OnBackgroundNonSimThread_Subclass( Faction faction, ArcenLongTermIntermittentPlanningContext Context )
        {
            // Load our data.
            // Start with world.
            CivilianWorld worldData = World.Instance.GetCivilianWorldExt();
            if ( worldData == null )
                return;

            // Next, do logic once for each faction that has a registered industry.
            for ( int x = 0; x < worldData.Factions.Count; x++ )
            {
                // Load in this faction's data.
                Faction playerFaction = World_AIW2.Instance.GetFactionByIndex( worldData.Factions[x] );
                if ( playerFaction == null )
                    continue;
                CivilianFaction factionData = playerFaction.GetCivilianFactionExt();
                if ( factionData == null )
                    continue;
                // Calculate the threat on all planets that need it.
                factionData.CalculateThreat( faction, playerFaction );
                DoCargoShipMovement( faction, playerFaction, factionData, Context );
                DoMilitiaCapitalShipMovement( faction, playerFaction, factionData, Context );
                DoMilitiaThreatReaction( faction, playerFaction, factionData, Context );
            }
        }

        // Check for our stuff dying.
        public override void DoOnAnyDeathLogic( GameEntity_Squad entity, EntitySystem FiringSystemOrNull, ArcenSimContext Context )
        {
            // Skip if the ship was not defined by our mod.
            // Things like spawnt patrol ships and turrets don't need to be processed for death.
            if ( !entity.TypeData.GetHasTag( "CivilianIndustryEntity" ) )
                return;

            // Load the faction data of the dead entity's faction.
            // Look through our world data, first, to find which faction controls our starting station, and load its faction data.
            CivilianWorld worldData = World.Instance.GetCivilianWorldExt();
            CivilianFaction factionData = null;
            // Look through our saved factions to find which our entity belongs to
            for ( int x = 0; x < worldData.Factions.Count; x++ )
            {
                CivilianFaction tempData = worldData.getFactionInfo( x ).factionData;
                if ( tempData.GrandStation == entity.PrimaryKeyID
                || tempData.CargoShips.Contains( entity.PrimaryKeyID )
                || tempData.MilitiaLeaders.Contains( entity.PrimaryKeyID )
                || tempData.TradeStations.Contains( entity.PrimaryKeyID ) )
                {
                    factionData = tempData;
                    break;
                }
            }

            // Deal with its death.
            if ( factionData.GrandStation == entity.PrimaryKeyID )
                factionData.GrandStation = -1;

            // Everything else; simply remove it from its respective list(s).
            if ( factionData.TradeStations.Contains( entity.PrimaryKeyID ) )
                factionData.TradeStations.Remove( entity.PrimaryKeyID );

            if ( factionData.CargoShips.Contains( entity.PrimaryKeyID ) )
                factionData.CargoShips.Remove( entity.PrimaryKeyID );

            if ( factionData.MilitiaLeaders.Contains( entity.PrimaryKeyID ) )
            {
                // Try to scrap all of its units.
                try
                {
                    entity.FleetMembership.Fleet.DoForEntities( delegate ( GameEntity_Squad squad )
                    {
                        if ( entity != squad )
                            squad.Despawn( Context, true, InstancedRendererDeactivationReason.SelfDestructOnTooHighOfCap );
                        return DelReturn.Continue;
                    } );
                }
                catch ( Exception )
                {
                    ArcenDebugging.SingleLineQuickDebug( "CivilianIndustries - Failed to find dead Militia fleet. Rogue units may be lieing around." );
                }
                factionData.MilitiaLeaders.Remove( entity.PrimaryKeyID );
            }

            for ( int x = 0; x < factionData.MilitiaLeaders.Count; x++ )
            {
                GameEntity_Squad militia = World_AIW2.Instance.GetEntityByID_Squad( factionData.MilitiaLeaders[x] );
                if ( militia == null )
                    continue;

                // If the trade station belongs to a militia planet, remove it.
            }

            // Save any changes.
            entity.PlanetFaction.Faction.SetCivilianFactionExt( factionData );
        }
    }
}