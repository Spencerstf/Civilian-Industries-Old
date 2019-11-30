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
        Idle,       // Doing nothing.
        Loading,    // Loading resources into ship.
        Unloading,  // Offloading resources onto a station.
        Pathing,    // Pathing towards a requesting station.
        Enroute     // Pathing towards a destination station.
    }

    // Used for militia ships for most of the same reason as the above.
    // Slightly more potential actions however.
    public enum CivilianMilitiaStatus
    {
        Idle,       // Doing nothing.
        Pathing,    // Pathing towards a trade station.
        Enroute,    // Moving into position next to a wormhole to deploy.
        Defending,  // In station form, requesting resources and building static defenses.
        Patrolling, // A more mobile form of Defense, requests resources to build mobile strike fleets.
        Assisting   // Attempting to attack a planet alongside the player.
    }

    // Enum used to keep track of resources used in this mod.
    // Only metal for now, but future-proofing for later expansion.
    public enum CivilianResource
    {
        Goods,      // Requirement for trade stations to function.
        Steel,
        Crystal,
        Aluminum,
        Silicon,
        Length
    }

    // Enum used to keep track of what ship requires what resource.
    public enum CivilianMilitiaShipType
    {
        None,   // Nothing is built by Goods.
        PikeCorvette,
        Eyebot,
        SentinelGunboat,
        Zapper,
        Length
    }

    // Enum used to keep track of what turret requires what resource.
    public enum CivilianMilitiaTurretType
    {
        None,   // Nothing is built by goods.
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

        // Index of ALL entities that either generate or use up resources.
        public List<int> ResourcePoints;

        // Index of all Militia Capital Ships and/or Militia Outposts
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
                            if (wave.gameTimeInSecondsForLaunchWave - World_AIW2.Instance.GameSecond <= 120)
                                hostileStrength += wave.CalculateStrengthOfWave( aiFaction ) * 5;
                        }
                    }
                    // Get hostile strength.
                    LongRangePlanningData_PlanetFaction linkedPlanetFactionData = planet.LongRangePlanningData.PlanetFactionDataByIndex[faction.FactionIndex];
                    LongRangePlanning_StrengthData_PlanetFaction_Stance hostileStrengthData = linkedPlanetFactionData.DataByStance[FactionStance.Hostile];
                    // If on allied planet, inflate threat massively. It deserves highest priority.
                    if ( planet.GetControllingFaction() == playerFaction )
                        hostileStrength += hostileStrengthData.TotalStrength * 10;
                    else
                        hostileStrength += hostileStrengthData.TotalStrength;

                    // If on home plant, double all direct (on planet and wave) threat.
                    if ( planet.PlanetIndex == grandPlanet.PlanetIndex )
                        hostileStrength *= 2;

                    // Adjacent planet threat matters as well, but not as much as direct threat.
                    if ( planet.GetControllingFaction() == playerFaction )
                        planet.DoForLinkedNeighbors( delegate ( Planet linkedPlanet )
                        {
                            // Get hostile strength.
                            linkedPlanetFactionData = linkedPlanet.LongRangePlanningData.PlanetFactionDataByIndex[faction.FactionIndex];
                            hostileStrengthData = linkedPlanetFactionData.DataByStance[FactionStance.Hostile];
                            hostileStrength += hostileStrengthData.TotalStrength;

                            // Reduce militia strength for each adjacent planet, to encourage starting buildings.
                            militiaStrength -= 10000;

                            return DelReturn.Continue;
                        } );

                    // Get friendly strength on the planet itself. (We never care about friendlies on adjacent planets.)
                    LongRangePlanningData_PlanetFaction planetFactionData = planet.LongRangePlanningData.PlanetFactionDataByIndex[faction.FactionIndex];
                    LongRangePlanning_StrengthData_PlanetFaction_Stance friendlyStrengthData = planetFactionData.DataByStance[FactionStance.Friendly];
                    friendlyStrength += friendlyStrengthData.TotalStrength;

                    // For each militia fleet that has its focus on this planet, subtract either its strength or a flat 10 from its threat, whichever is higher.
                    // The bigger threats should keep having more fleets thrown at them for as long as possible, without taking absolutely everything.
                    for ( int x = 0; x < MilitiaLeaders.Count; x++ )
                    {
                        GameEntity_Squad militiaCapital = World_AIW2.Instance.GetEntityByID_Squad( MilitiaLeaders[x] );
                        if ( militiaCapital == null )
                            continue;
                        CivilianMilitia militiaData = militiaCapital.GetCivilianMilitiaExt();
                        if ( militiaData == null )
                            continue;
                        if ( militiaData.PlanetFocus == planet.PlanetIndex )
                        {
                            // Intensity modifier. Higher the intensity, the more aggressively the faction will defend itself.
                            int intensityModifier = (int)Math.Pow( 25000, 1 - (faction.Ex_MinorFactionCommon_GetPrimitives().Intensity / 100) );
                            militiaStrength += Math.Min( 10000, Math.Max( intensityModifier, militiaCapital.FleetMembership.Fleet.CalculateEffectiveCurrentFleetStrength() ) );
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

        // Returns the request points per ship.
        public int GetRequestPoints()
        {
            return (11 - World_AIW2.Instance.GetEntityByID_Squad( GrandStation ).PlanetFaction.Faction.Ex_MinorFactionCommon_GetPrimitives().Intensity) * 10;
        }

        // Returns the resource cost per ship/turret.
        public int GetResourceCost( int count )
        {
            // (50 - (Intensity ^ 1.5)) ^ (1 + #OfBuiltShips/100)
            return (int)Math.Pow( 50 - (int)Math.Pow( World_AIW2.Instance.GetEntityByID_Squad( GrandStation ).PlanetFaction.Faction.Ex_MinorFactionCommon_GetPrimitives().Intensity, 1.5 ),
                1 + count / 100 );
        }

        // Returns the current capacity for turrets/ships.
        public int GetCap()
        {
            // 10 + (AIP / 5) ^ (1 + (Intensity/100) 
            int cap = 0;
            for ( int y = 0; y < World_AIW2.Instance.AIFactions.Count; y++ )
                cap = (int)(Math.Ceiling( Math.Pow( Math.Max( cap, 10 + World_AIW2.Instance.AIFactions[y].GetAICommonExternalData().AIProgress_Total.ToInt() / 5 ),
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
            this.ResourcePoints = new List<int>();
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
            count = this.ResourcePoints.Count;
            Buffer.AddItem( count );
            for ( int x = 0; x < count; x++ )
                Buffer.AddItem( this.ResourcePoints[x] );
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
            this.ResourcePoints = new List<int>();
            this.MilitiaLeaders = new List<int>();
            int count = Buffer.ReadInt32();
            for ( int x = 0; x < count; x++ )
                this.TradeStations.Add( Buffer.ReadInt32() );
            count = Buffer.ReadInt32();
            for ( int x = 0; x < count; x++ )
                this.CargoShips.Add( Buffer.ReadInt32() );
            count = Buffer.ReadInt32();
            for ( int x = 0; x < count; x++ )
                this.ResourcePoints.Add( Buffer.ReadInt32() );
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
            return other.GetThreat().Actual.CompareTo( this.GetThreat().Actual );
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

        // Wormhole that this fleet has been assigned to. If -1, its a roaming defender instead.
        public int Wormhole;

        // Resources stored towards ship production.
        public int[] ProcessedResources;

        // Following three functions are used for initializing, saving, and loading data.
        // Initialization function.
        // Default values. Called on creation, NOT on load.
        public CivilianMilitia()
        {
            this.Status = CivilianMilitiaStatus.Idle;
            this.PlanetFocus = -1;
            this.Wormhole = -1;
            this.ProcessedResources = new int[(int)CivilianResource.Length];
            for ( int x = 0; x < this.ProcessedResources.Length; x++ )
                this.ProcessedResources[x] = 0;
        }
        // Saving our data.
        public void SerializeTo( ArcenSerializationBuffer Buffer )
        {
            Buffer.AddItem( (int)this.Status );
            Buffer.AddItem( this.PlanetFocus );
            Buffer.AddItem( this.Wormhole );
            int count = this.ProcessedResources.Length;
            Buffer.AddItem( count );
            for ( int x = 0; x < count; x++ )
                Buffer.AddItem( this.ProcessedResources[x] );
        }
        // Loading our data. Make sure the loading order is the same as the saving order.
        public CivilianMilitia( ArcenDeserializationBuffer Buffer )
        {
            this.Status = (CivilianMilitiaStatus)Buffer.ReadInt32();
            this.PlanetFocus = Buffer.ReadInt32();
            this.Wormhole = Buffer.ReadInt32();
            this.ProcessedResources = new int[(int)CivilianResource.Length];
            int count = Buffer.ReadInt32();
            for ( int x = 0; x < count; x++ )
                this.ProcessedResources[x] = Buffer.ReadInt32();
        }

        public GameEntity_Other getWormhole()
        {
            return World_AIW2.Instance.GetEntityByID_Other( this.Wormhole );
        }
    }

    // Description classes.
    // Handles informing the player of what buildings and ships in this faction are doing.
    // Must be set to be used in the xml for whatever entity you want this to be applied to.
    // As an example for our first appender, we'd want to put the following two lines on our station in the xml file.
    // description_appender_dll="SKPlayerTrains" - What file all of our eventual compiled code will be going into. 
    // description_appender_type="Arcen.AIW2.SK.GrandStationDescriptionAppender" - The name of the class it'll be using.

    // Grand Stations
    // Used to display stored cargo and the faction's build counter.
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
            // Load our cargo data.
            CivilianCargo cargoData = RelatedEntityOrNull.GetCivilianCargoExt();

            // Infinite Goods.
            Buffer.Add( "\nInfinite supply of Goods" );

            // Inform them about what the station has on it.
            for ( int x = 1; x < cargoData.Amount.Length; x++ )
            {
                if ( cargoData.Amount[x] > 0 || cargoData.PerSecond[x] != 0 )
                {
                    Buffer.Add( "\n" + cargoData.Amount[x] + "/" + cargoData.Capacity[x] + " " + ((CivilianResource)x).ToString() );
                    // If resource has generation or drain, notify them.
                    if ( cargoData.PerSecond[x] != 0 )
                        Buffer.Add( " +" + cargoData.PerSecond[x] + " per second" );
                }
            }

            // If we found our faction data, inform them about build requests in the faction.
            if ( factionData != null )
            {
                Buffer.Add( "\n" + factionData.BuildCounter + "/" + factionData.CargoShips.Count * factionData.GetRequestPoints() + " Request points until next Cargo Ship built." );
                Buffer.Add( "\n" + factionData.MilitiaCounter + "/" + factionData.MilitiaLeaders.Count * factionData.GetRequestPoints() + " Request points until next Miltia ship built." );
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
                Buffer.Add( " towards " + World_AIW2.Instance.GetEntityByID_Squad( shipStatus.Destination ).Planet.Name );
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
            CivilianCargo cargoData = RelatedEntityOrNull.GetCivilianCargoExt();

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

            for ( int x = 0; x < cargoData.Amount.Length; x++ )
            {
                if ( ((CivilianResource)x) == CivilianResource.Goods || ((CivilianResource)x) == CivilianResource.Length )
                    continue;
                if ( cargoData.Capacity[x] > 0 )
                {
                    // Forces information.
                    GameEntityTypeData typeData = null;
                    if ( militiaData.Status == CivilianMilitiaStatus.Defending )
                        typeData = GameEntityTypeDataTable.Instance.GetRowByName( ((CivilianMilitiaTurretType)x).ToString(), false, null );
                    else if ( militiaData.Status == CivilianMilitiaStatus.Patrolling )
                        typeData = GameEntityTypeDataTable.Instance.GetRowByName( ((CivilianMilitiaShipType)x).ToString(), false, null );
                    int count;
                    try
                    {
                        count = RelatedEntityOrNull.FleetMembership.Fleet.GetButDoNotAddMembershipGroupBasedOnSquadType_AssumeNoDuplicates( typeData ).Entities.Count;
                    }
                    catch ( Exception )
                    {
                        count = 0;
                    }

                    // Display only if we have already received a resource of the type.
                    if ( cargoData.Amount[x] > 0 || count > 0 || militiaData.ProcessedResources[x] > 0 )
                    {

                        // Cargo information.
                        Buffer.Add( "\n" + cargoData.Amount[x] + "/" + cargoData.Capacity[x] + " " + ((CivilianResource)x).ToString() );

                        // If one is built, or it has the resources to begin building one, display it.
                        if ( count > 0 || cargoData.Amount[x] > 0 )
                        {
                            Buffer.Add( " " + count + "/" + factionData.GetCap() + " " + typeData.DisplayName );
                            // If at least one metal, list time until next.
                            if ( count >= factionData.GetCap() )
                                Buffer.Add( " (Building paused, max capacity.)" );
                            else if ( cargoData.Amount[x] > 0 )
                                Buffer.Add( " (Processing " + ((CivilianResource)x).ToString() + ", " + ((factionData.GetResourceCost( count )) - militiaData.ProcessedResources[x]) + " seconds left.)" );
                            else
                                Buffer.Add( " (Building paused, no " + ((CivilianResource)x).ToString() + ".)" );
                        }
                    }
                }
            }
            // Inform them about what the ship is doing.
            Buffer.Add( "\nThis ship is currently " + militiaData.Status.ToString() + "." );

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
                 factionData.ResourcePoints.Add( grandStation.PrimaryKeyID );

                 // Initialize cargo.
                 // Grand station has an incredibly high capacity of every resource, and produces an essentially infinite supply of goods.
                 CivilianCargo grandCargo = grandStation.GetCivilianCargoExt();
                 grandCargo.Amount[(int)CivilianResource.Goods] = grandCargo.Capacity[(int)CivilianResource.Goods] / 2;
                 grandCargo.PerSecond[(int)CivilianResource.Goods] = 10;
                 for ( int y = 0; y < grandCargo.Capacity.Length; y++ )
                     grandCargo.Capacity[y] *= 100;
                 grandStation.SetCivilianCargoExt( grandCargo );

                 return DelReturn.Break;
             } );
        }

        // Handle creation of trade stations.
        public void CreateTradeStations( Faction faction, Faction playerFaction, CivilianFaction factionData, ArcenSimContext Context )
        {
            playerFaction.Entities.DoForEntities( EntityRollupType.CommandStation, delegate ( GameEntity_Squad commandStation )
             {
                 // Skip if king unit.
                 if ( commandStation.TypeData.SpecialType == SpecialEntityType.HumanHomeCommand )
                     return DelReturn.Continue;

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
                 factionData.ResourcePoints.Add( tradeStation.PrimaryKeyID );

                 // Initialize cargo.
                 CivilianCargo tradeCargo = tradeStation.GetCivilianCargoExt();
                 // Large capacity.
                 for ( int y = 0; y < tradeCargo.Capacity.Length; y++ )
                     tradeCargo.Capacity[y] *= 10;
                 // Drains 1 Goods per second to remain active, starts with a small stockpile.
                 tradeCargo.PerSecond[(int)CivilianResource.Goods] = -1;
                 // Starts with a healthy supply of Goods to keep itself running.
                 tradeCargo.Amount[(int)CivilianResource.Goods] = tradeCargo.Capacity[(int)CivilianResource.Goods];
                 // Generates 1 random resource per second for every mine on the planet.
                 int mines = 0;
                 tradeStation.Planet.DoForEntities( EntityRollupType.MetalProducers, delegate ( GameEntity_Squad mineEntity )
                 {
                     if ( mineEntity.TypeData.GetHasTag( "MetalGenerator" ) )
                         mines++;

                     return DelReturn.Continue;
                 } );
                 tradeCargo.PerSecond[Context.RandomToUse.Next( (int)CivilianResource.Goods + 1, (int)CivilianResource.Length )] = mines;

                 tradeStation.SetCivilianCargoExt( tradeCargo );

                 return DelReturn.Continue;
             } );
        }

        // Handle resource processing.
        public void DoResources( Faction faction, Faction playerFaction, CivilianFaction factionData, ArcenSimContext Context )
        {
            // For every ResourcePoint we have defined in our faction data, deal with it.
            for ( int x = 0; x < factionData.ResourcePoints.Count; x++ )
            {
                // Load the entity, and its cargo data.
                GameEntity_Squad entity = World_AIW2.Instance.GetEntityByID_Squad( factionData.ResourcePoints[x] );
                CivilianCargo entityCargo = entity.GetCivilianCargoExt();

                // If Grand Station, max out its goods.
                if ( entity.PrimaryKeyID == factionData.GrandStation )
                {
                    entityCargo.Amount[(int)CivilianResource.Goods] = entityCargo.Capacity[(int)CivilianResource.Goods];
                    continue;
                }

                // If it needs good to function, pay it.
                bool paid = true;
                if ( entityCargo.PerSecond[(int)CivilianResource.Goods] < 0 )
                {
                    // Make sure it has enough resources to cover its cost.
                    if ( entityCargo.Amount[(int)CivilianResource.Goods] < Math.Abs( entityCargo.PerSecond[(int)CivilianResource.Goods] ) )
                        paid = false;
                    else
                        entityCargo.Amount[(int)CivilianResource.Goods] = Math.Min( entityCargo.Capacity[(int)CivilianResource.Goods], entityCargo.Amount[(int)CivilianResource.Goods] + entityCargo.PerSecond[(int)CivilianResource.Goods] );
                }

                if ( !paid )
                    continue;

                // Now, deal with its per second values.
                for ( int y = 1; y < entityCargo.PerSecond.Length; y++ )
                {
                    if ( entityCargo.PerSecond[y] != 0 )
                    {
                        // Update the resource, if able.
                        if ( entityCargo.PerSecond[y] > 0 || (entityCargo.Amount[y] >= Math.Abs( entityCargo.PerSecond[y] )) )
                        {
                            // Building logic.
                            // Militia buildings.
                            if ( entity.TypeData.GetHasTag( "MilitiaOutpost" ) )
                            {
                                // If militia ship count is below cap, process the resource.
                                // Get ship or turret type based on resource.
                                GameEntityTypeData typeData = GameEntityTypeDataTable.Instance.GetRowByName( ((CivilianMilitiaTurretType)y).ToString(), false, null );
                                int count;
                                try
                                {
                                    count = entity.FleetMembership.Fleet.GetButDoNotAddMembershipGroupBasedOnSquadType_AssumeNoDuplicates( typeData ).Entities.Count;
                                }
                                catch ( Exception )
                                {
                                    count = 0;
                                }
                                // If count is below cap, proccess.
                                if ( count < factionData.GetCap() )
                                {
                                    entityCargo.Amount[y] = Math.Min( entityCargo.Capacity[y], entityCargo.Amount[y] + entityCargo.PerSecond[y] );
                                    CivilianMilitia militiaData = entity.GetCivilianMilitiaExt();
                                    militiaData.ProcessedResources[y] -= entityCargo.PerSecond[y];
                                    entity.SetCivilianMilitiaExt( militiaData );
                                }
                            }
                            else if ( entity.TypeData.GetHasTag( "MilitiaBarracks" ) )
                            {
                                // If militia ship count is below cap, process the resource.
                                // Get ship or turret type based on resource.
                                GameEntityTypeData typeData = GameEntityTypeDataTable.Instance.GetRowByName( ((CivilianMilitiaShipType)y).ToString(), false, null );
                                int count;
                                try
                                {
                                    count = entity.FleetMembership.Fleet.GetButDoNotAddMembershipGroupBasedOnSquadType_AssumeNoDuplicates( typeData ).Entities.Count;
                                }
                                catch ( Exception )
                                {
                                    count = 0;
                                }
                                // If count is below cap, proccess.
                                if ( count < factionData.GetCap() )
                                {
                                    entityCargo.Amount[y] = Math.Min( entityCargo.Capacity[y], entityCargo.Amount[y] + entityCargo.PerSecond[y] );
                                    CivilianMilitia militiaData = entity.GetCivilianMilitiaExt();
                                    militiaData.ProcessedResources[y] -= entityCargo.PerSecond[y];
                                    entity.SetCivilianMilitiaExt( militiaData );
                                }
                            }
                            // Other Buildings, process.
                            else
                            {
                                entityCargo.Amount[y] = Math.Min( entityCargo.Capacity[y], entityCargo.Amount[y] + entityCargo.PerSecond[y] );
                            }
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

            // Build a cargo ship if one of the following is true:
            // Less than 10 cargo ships.
            // More than 10 cargo ships, and build counter is > # of cargo ships * 100.
            if ( factionData.CargoShips.Count < 10 || factionData.BuildCounter > factionData.CargoShips.Count * factionData.GetRequestPoints() )
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

            // Build a militia ship if one of the following is true:
            // Less than 1 militia ships.
            // More than 1 militia ship, and build counter is > # of militia ships * 50.
            if ( factionData.MilitiaLeaders.Count < 1 || factionData.MilitiaCounter > factionData.MilitiaLeaders.Count * factionData.GetRequestPoints() )
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

                // Initialize cargo.
                CivilianCargo tradeCargo = entity.GetCivilianCargoExt();
                // Large capacity of everything but goods.
                for ( int y = 0; y < tradeCargo.Capacity.Length; y++ )
                    if ( ((CivilianResource)y) == CivilianResource.Goods )
                        tradeCargo.Capacity[y] = 0;
                    else
                    {
                        tradeCargo.Capacity[y] *= 15;
                        tradeCargo.PerSecond[y] = -1;
                    }
                entity.SetCivilianCargoExt( tradeCargo );

                // Add the militia ship to our faction data.
                factionData.MilitiaLeaders.Add( entity.PrimaryKeyID );

                // Reset the build counter.
                factionData.MilitiaCounter = 0;

                // Create a new fleet with our ship as its centerpiece.
                Fleet militiaFleet = Fleet.Create( FleetCategory.NPC, faction, entity.PlanetFaction, entity );
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

                // Pathing logic, detect arrival at stations.
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
                        shipStatus.Status = CivilianShipStatus.Unloading;
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

                    // Send the resources, if the station has any left.
                    for ( int y = 0; y < (int)CivilianResource.Length; y++ )
                    {
                        // If station doesn't produce the resource, the ship has it on them, and the station isn't full of it, deposit it to the station.
                        if ( originCargo.PerSecond[y] < 1 && shipCargo.Amount[y] > 0 && originCargo.Amount[y] < originCargo.Capacity[y] )
                        {
                            shipCargo.Amount[y]--;
                            originCargo.Amount[y]++;
                        }
                        // Otherwise, do Loading logic.
                        else
                        {
                            // Stop if there are no resources left to load, if its a resource the station uses, if its the grand station and its not goods,
                            // or if the ship is full.
                            if ( originCargo.Amount[y] <= 0 || originCargo.PerSecond[y] < 0
                             || (originStation.PrimaryKeyID == factionData.GrandStation && y != (int)CivilianResource.Goods)
                             || shipCargo.Amount[y] >= shipCargo.Capacity[y] )
                                continue;

                            // Transfer a single resource per second.
                            originCargo.Amount[y]--;
                            shipCargo.Amount[y]++;
                        }
                    }

                    // Save the resources.
                    originStation.SetCivilianCargoExt( originCargo );
                    cargoShip.SetCivilianCargoExt( shipCargo );

                    // If load timer hit 0, stop loading.
                    if ( shipStatus.LoadTimer <= 0 )
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
            }
        }

        // Handle station requests.
        public void DoTradeRequests( Faction faction, Faction playerFaction, CivilianFaction factionData, ArcenSimContext Context )
        {
            // Get a list of all station's urgency for all values.

            // Skip if there aren't at least two resource points.
            if ( factionData.ResourcePoints.Count < 2 )
                return;

            // Get a list of TradeRequests to be later sorted.
            List<TradeRequest> tradeRequests = new List<TradeRequest>();

            // Populate our TradeRequests list.
            for ( int x = 0; x < factionData.ResourcePoints.Count; x++ )
            {
                GameEntity_Squad requester = World_AIW2.Instance.GetEntityByID_Squad( factionData.ResourcePoints[x] );
                if ( requester == null )
                {
                    // Remove invalid ResourcePoints.
                    factionData.ResourcePoints.RemoveAt( x );
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

                    // Resources we generate.
                    if ( requesterCargo.PerSecond[y] > 0 )
                    {
                        // Generates urgency based on how close to full capacity we are.
                        int urgency = (int)Math.Ceiling( (double)(1 - ((requesterCargo.Capacity[y] - requesterCargo.Amount[y]) / requesterCargo.Capacity[y])) * 4 );
                        if ( urgency > 0 )
                        {
                            // Lower urgency for each ship inbound to pickup.
                            for ( int z = 0; z < factionData.CargoShips.Count; z++ )
                            {
                                GameEntity_Squad cargoShip = World_AIW2.Instance.GetEntityByID_Squad( factionData.CargoShips[z] );
                                if ( cargoShip == null )
                                    continue;
                                CivilianStatus cargoStatus = cargoShip.GetCivilianStatusExt();
                                if ( cargoShip.GetCivilianStatusExt().Status == CivilianShipStatus.Pathing
                                  && cargoStatus.Origin == factionData.ResourcePoints[x] )
                                    urgency--;
                            }
                            // If urgency is still above 0, create a new trade request.
                            if ( urgency > 0 )
                                tradeRequests.Add( new TradeRequest( (CivilianResource)y, urgency, true, requester ) );
                        }
                    }
                    // Resources we use.
                    else if ( requesterCargo.PerSecond[y] < 0 )
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
                                  && cargoStatus.Destination == factionData.ResourcePoints[x] )
                                    urgency--;
                            }
                            // If urgency is still above 0, create a new trade request.
                            if ( urgency > 0 )
                                tradeRequests.Add( new TradeRequest( (CivilianResource)y, urgency, false, requester ) );
                        }
                    }
                    // Resource we store. Simply put out a super tiny order to import/export based on current stores.
                    else
                    {
                        if ( requesterCargo.Amount[y] > requesterCargo.Capacity[y] * 0.8 )
                            tradeRequests.Add( new TradeRequest( (CivilianResource)y, 1, true, requester ) );
                        else
                            tradeRequests.Add( new TradeRequest( (CivilianResource)y, 1, false, requester ) );
                    }
                }
            }

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

            // If there are no free CargoShips, simply add all Urgency values to the Grand Station.
            if ( freeShipIndexes.Count == 0 )
            {
                factionData.BuildCounter += (from o in tradeRequests select o.Urgency).Sum();
                return;
            }

            // Sort our list.
            tradeRequests.Sort();

            // Initially limit the number of hops to search through, to try and find closer matches to start with.
            // While we have free ships left, assign our requests away.
            int numOfHops = 0;
            while ( numOfHops <= 10 )
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
                    // If we failed to find a station, and we're looking for goods, we can always get them from the grand station.
                    // So lets do that, assuming its within hop radius.
                    if ( otherStation == null && tradeRequests[x].Requested == CivilianResource.Goods
                      && requestingEntity.PrimaryKeyID != factionData.GrandStation
                      && requestingEntity.Planet.GetHopsTo( World_AIW2.Instance.GetEntityByID_Squad( factionData.GrandStation ).Planet ) < numOfHops )
                        otherStation = World_AIW2.Instance.GetEntityByID_Squad( factionData.GrandStation );
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
            // If we've finished due to not having enough trade ships, request more!
            if ( tradeRequests.Count > 0 && freeShipIndexes.Count == 0 )
                factionData.BuildCounter += (from o in tradeRequests select o.Urgency).Sum();
        }

        // Handle assigning militia to our ThreatReports.
        public void DoMilitiaAssignment( Faction faction, Faction playerFaction, CivilianFaction factionData, ArcenSimContext Context )
        {
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
                    factionData.MilitiaCounter += ((factionData.ThreatReports[x].GetThreat().Hostile - factionData.ThreatReports[x].GetThreat().Militia) / 1000);
                    continue;
                }

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

                // Assign our militia to the planet.
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

                     bool wormholeTaken = false;

                     // If its not been claimed by another militia, claim it.
                     for ( int y = 0; y < factionData.MilitiaLeaders.Count; y++ )
                     {
                         // Load and compare its status's wormhole focus.
                         GameEntity_Squad otherMilitia = World_AIW2.Instance.GetEntityByID_Squad( factionData.MilitiaLeaders[y] );
                         if ( otherMilitia == null )
                             continue;

                         if ( militia.PrimaryKeyID == otherMilitia.PrimaryKeyID )
                             continue;

                         CivilianMilitia otherMilitiaStatus = otherMilitia.GetCivilianMilitiaExt();
                         if ( otherMilitiaStatus == null )
                             continue;

                         if ( wormhole.PrimaryKeyID == otherMilitiaStatus.Wormhole )
                             wormholeTaken = true;
                     }

                     if ( !wormholeTaken )
                     {
                         // If its not been claimed, claim it.
                         foundWormhole = wormhole;
                         return DelReturn.Break;
                     }
                     return DelReturn.Continue;
                 } );

                // Update the militia's status.
                CivilianMilitia militiaStatus = militia.GetCivilianMilitiaExt();
                militiaStatus.PlanetFocus = factionData.ThreatReports[x].Planet.PlanetIndex;
                militiaStatus.Status = CivilianMilitiaStatus.Pathing;

                // If we found a wormhole, assign it.
                if ( foundWormhole != null )
                {
                    militiaStatus.Wormhole = foundWormhole.PrimaryKeyID;
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
                     if ( factionData.TradeStations.Contains( entity.PrimaryKeyID ) || factionData.GrandStation == entity.PrimaryKeyID )
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
                if ( militiaStatus.Status == CivilianMilitiaStatus.Pathing )
                {
                    // If nearby, advance status.
                    if ( militiaShip.GetDistanceTo_ExpensiveAccurate( goalStation.WorldLocation, true, true ) < 500 )
                    {
                        // Set as Enroute, if they have a wormhole.
                        if ( militiaStatus.Wormhole != -1 )
                            militiaStatus.Status = CivilianMilitiaStatus.Enroute;
                        else
                        {
                            // Otherwise, convert them into a Militia Barracks and begin ship production.
                            // Prepare its old id to be removed.
                            toRemove.Add( militiaShip.PrimaryKeyID );

                            // Get hold of its old Fleet.
                            Fleet oldFleet = militiaShip.FleetMembership.Fleet;

                            // Load its cargo, so it can be sent over to its new entity.
                            CivilianCargo militiaCargo = militiaShip.GetCivilianCargoExt();

                            // Converting to a Barracks, upgrade the fleet status to a mobile patrol.
                            militiaStatus.Status = CivilianMilitiaStatus.Patrolling;

                            // Load its station data.
                            GameEntityTypeData outpostData = GameEntityTypeDataTable.Instance.GetRandomRowWithTag( Context, "MilitiaBarracks" );

                            // Transform it.
                            GameEntity_Squad newMilitiaShip = militiaShip.TransformInto( Context, outpostData, 1 );

                            // Make sure its not overlapping.
                            newMilitiaShip.SetWorldLocation( newMilitiaShip.Planet.GetSafePlacementPoint( Context, outpostData, newMilitiaShip.WorldLocation, 0, 1000 ) );

                            // Move the information to our new ship.
                            newMilitiaShip.SetCivilianMilitiaExt( militiaStatus );
                            newMilitiaShip.SetCivilianCargoExt( militiaCargo );

                            // Create a new fleet, and transfer over our old fleet info to it.
                            Fleet newFleet = Fleet.Create( FleetCategory.NPC, faction, newMilitiaShip.PlanetFaction, newMilitiaShip );
                            oldFleet.DoForEntities( delegate ( GameEntity_Squad oldEntity )
                             {
                                 // Skip centerpiece.
                                 if ( oldEntity.PrimaryKeyID == oldFleet.Centerpiece.PrimaryKeyID )
                                     return DelReturn.Continue;

                                 newFleet.AddSquadToMembership_AssumeNoDuplicates( oldEntity );

                                 return DelReturn.Continue;
                             } );

                            // Prepare its new id to be added.
                            toAdd.Add( newMilitiaShip.PrimaryKeyID );

                            // Add the ship to our resource points for processing.
                            factionData.ResourcePoints.Add( newMilitiaShip.PrimaryKeyID );
                        }
                    }
                }
                else if ( militiaStatus.Status == CivilianMilitiaStatus.Enroute ) // If enroute, check for sweet spot.
                {
                    int stationDist = militiaShip.GetDistanceTo_ExpensiveAccurate( goalStation.WorldLocation, true, true );
                    int wormDist = militiaShip.GetDistanceTo_ExpensiveAccurate( militiaStatus.getWormhole().WorldLocation, true, true );
                    int range = 8000;
                    if ( stationDist > range * 0.2 &&
                        (stationDist > range * 0.6 || wormDist < range) )
                    {
                        // Prepare its old id to be removed.
                        toRemove.Add( militiaShip.PrimaryKeyID );

                        // Get hold of its old Fleet.
                        Fleet oldFleet = militiaShip.FleetMembership.Fleet;

                        // Load its cargo, so it can be sent over to its new entity.
                        CivilianCargo militiaCargo = militiaShip.GetCivilianCargoExt();
                        // Optimal distance. Transform the ship and update its status.
                        militiaStatus.Status = CivilianMilitiaStatus.Defending;

                        // Load its station data.
                        GameEntityTypeData outpostData = GameEntityTypeDataTable.Instance.GetRandomRowWithTag( Context, "MilitiaOutpost" );

                        // Transform it.
                        GameEntity_Squad newMilitiaShip = militiaShip.TransformInto( Context, outpostData, 1 );

                        // Move the information to our new ship.
                        newMilitiaShip.SetCivilianMilitiaExt( militiaStatus );
                        newMilitiaShip.SetCivilianCargoExt( militiaCargo );

                        // Create a new fleet, and transfer over our old fleet info to it.
                        Fleet newFleet = Fleet.Create( FleetCategory.NPC, faction, newMilitiaShip.PlanetFaction, newMilitiaShip );
                        oldFleet.DoForEntities( delegate ( GameEntity_Squad oldEntity )
                        {
                            // Skip centerpiece.
                            if ( oldEntity.PrimaryKeyID == oldFleet.Centerpiece.PrimaryKeyID )
                                return DelReturn.Continue;

                            newFleet.AddSquadToMembership_AssumeNoDuplicates( oldEntity );

                            return DelReturn.Continue;
                        } );

                        // Prepare its new id to be added.
                        toAdd.Add( newMilitiaShip.PrimaryKeyID );

                        // Add the ship to our resource points for processing.
                        factionData.ResourcePoints.Add( newMilitiaShip.PrimaryKeyID );
                    }
                }
                else if ( militiaStatus.Status == CivilianMilitiaStatus.Defending ) // If defending, do turret placement.
                {
                    CivilianCargo militiaCargo = militiaShip.GetCivilianCargoExt();
                    // For each type of unit, get turret count.
                    for ( int y = 1; y < (int)CivilianResource.Length; y++ )
                    {
                        GameEntityTypeData typeData = GameEntityTypeDataTable.Instance.GetRowByName( ((CivilianMilitiaTurretType)y).ToString(), false, null );
                        int count;
                        try
                        {
                            count = militiaShip.FleetMembership.Fleet.GetButDoNotAddMembershipGroupBasedOnSquadType_AssumeNoDuplicates( typeData ).Entities.Count;
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
                    // Save our militia's status and cargo.
                    militiaShip.SetCivilianMilitiaExt( militiaStatus );
                    militiaShip.SetCivilianCargoExt( militiaCargo );
                }
                else if ( militiaStatus.Status == CivilianMilitiaStatus.Patrolling ) // If patrolling, do unit spawning.
                {
                    CivilianCargo militiaCargo = militiaShip.GetCivilianCargoExt();
                    // For each type of unit, get ship count.
                    for ( int y = 1; y < (int)CivilianResource.Length; y++ )
                    {
                        GameEntityTypeData typeData = GameEntityTypeDataTable.Instance.GetRowByName( ((CivilianMilitiaShipType)y).ToString(), false, null );
                        int count;
                        try
                        {
                            count = militiaShip.FleetMembership.Fleet.GetButDoNotAddMembershipGroupBasedOnSquadType_AssumeNoDuplicates( typeData ).Entities.Count;
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
                        }
                    }
                    // Save our militia's status and cargo.
                    militiaShip.SetCivilianMilitiaExt( militiaStatus );
                    militiaShip.SetCivilianCargoExt( militiaCargo );
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

            // Update the mark level of all units.
            double mark = 0;
            for ( int x = 0; x < World_AIW2.Instance.AIFactions.Count; x++ )
                mark += (double)World_AIW2.Instance.AIFactions[x].GetAICommonExternalData().AIProgress_Effective.ToInt() / 100;
            faction.Entities.DoForEntities( delegate ( GameEntity_Squad squad )
             {
                 squad.SetCurrentMarkLevel( (int)Math.Floor( mark ), Context );
                 return DelReturn.Continue;
             } );

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
                if ( factionData.GrandStation == -1 )
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

                // Calculate the threat on all planets that need it.
                factionData.CalculateThreat( faction, playerFaction );

                // Handle assigning militia to our ThreatReports.
                DoMilitiaAssignment( faction, playerFaction, factionData, Context );

                // Handle militia deployment and unit building.
                DoMilitiaDeployment( faction, playerFaction, factionData, Context );

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
                if ( shipStatus.Status == CivilianMilitiaStatus.Pathing )
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
                             if ( factionData.TradeStations.Contains( entity.PrimaryKeyID ) || factionData.GrandStation == entity.PrimaryKeyID )
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
                else if ( shipStatus.Status == CivilianMilitiaStatus.Enroute )
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
                    command.RelatedPoints.Add( ship.WorldLocation.GetPointAtAngleAndDistance( ship.WorldLocation.GetAngleToDegrees( shipStatus.getWormhole().WorldLocation ), 5000 ) );

                    // Have the command apply to our ship.
                    command.RelatedEntityIDs.Add( ship.PrimaryKeyID );

                    // Tell the game to apply our command.
                    Context.QueueCommandForSendingAtEndOfContext( command );
                }
                else if ( shipStatus.Status == CivilianMilitiaStatus.Patrolling )
                {
                    // Patrolling movement.
                    // Have the ship and all of its forces patrol around the planet, taking Metal from it to fund itself as applicable.
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

            // Process all militia forces that are currently patrolling.
            #region Defensive Actions
            for ( int x = 0; x < factionData.MilitiaLeaders.Count; x++ )
            {
                GameEntity_Squad centerpiece = World_AIW2.Instance.GetEntityByID_Squad( factionData.MilitiaLeaders[x] );
                if ( centerpiece == null || centerpiece.GetCivilianMilitiaExt().Status != CivilianMilitiaStatus.Patrolling )
                    continue;

                // Where are we going to send all our units?
                Planet targetPlanet = null;

                // Is it a high priority?
                bool highPriority = false;

                // If self or an adjacent friendly planet has hostile units on it that outnumber friendly defenses, including incoming waves, protect it.
                for ( int y = 0; y < factionData.ThreatReports.Count && targetPlanet == null; y++ )
                {
                    ThreatReport report = factionData.ThreatReports[y];

                    if ( report.GetThreat().Hostile - report.GetThreat().Friendly > 0
                     && report.Planet.GetControllingFaction() == playerFaction
                     && report.Planet.GetHopsTo( centerpiece.Planet ) <= 1 )
                    {
                        targetPlanet = report.Planet;
                        // If its us or our Grand Station's planet, rush there.
                        if ( (World_AIW2.Instance.GetEntityByID_Squad( factionData.GrandStation ) != null
                         && World_AIW2.Instance.GetEntityByID_Squad( factionData.GrandStation ).Planet == targetPlanet)
                         || targetPlanet == centerpiece.Planet )
                            highPriority = true;
                    }
                }

                // If we have a target for defensive action, act on it.
                if ( targetPlanet != null )
                {
                    centerpiece.FleetMembership.Fleet.DoForEntities( delegate ( GameEntity_Squad entity )
                    {
                        // If its not urgent, and we have hostile forces on our current planet, finish our fight first.
                        if ( factionData.GetThreat( entity.Planet ).Actual > 0 && !highPriority )
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
                else
                {
                    // If we have at least one planet adjacent to us that is hostile, add to the raiding pool.
                    if ( (from o in factionData.ThreatReports where o.Planet.GetHopsTo( centerpiece.Planet ) == 1 select o).Count() > 0 )
                    {
                        if ( raidStrength.ContainsKey( centerpiece.Planet ) )
                            raidStrength[centerpiece.Planet] += (centerpiece.FleetMembership.Fleet.CalculateEffectiveCurrentFleetStrength() - centerpiece.GetStrengthOfSelfAndContents());
                        else
                            raidStrength.Add( centerpiece.Planet, (centerpiece.FleetMembership.Fleet.CalculateEffectiveCurrentFleetStrength() - centerpiece.GetStrengthOfSelfAndContents()) );
                    }
                    else
                    {
                        // Otherwise, send our units to the planet adjacen to us with the largest threat, negative or otherwise.
                        for ( int y = 0; y < factionData.ThreatReports.Count && targetPlanet == null; y++ )
                        {
                            ThreatReport report = factionData.ThreatReports[y];
                            if ( report.Planet != centerpiece.Planet
                             && report.Planet.GetControllingFaction() == playerFaction
                             && report.Planet.GetHopsTo( centerpiece.Planet ) == 1 )
                            {
                                targetPlanet = report.Planet;
                                // If its our Grand Station's planet, rush there.
                                if ( World_AIW2.Instance.GetEntityByID_Squad( factionData.GrandStation ) != null
                                 && World_AIW2.Instance.GetEntityByID_Squad( factionData.GrandStation ).Planet == targetPlanet )
                                {
                                    highPriority = true;
                                }
                                break;
                            }
                        }
                        centerpiece.FleetMembership.Fleet.DoForEntities( delegate ( GameEntity_Squad entity )
                        {
                            // If its not urgent, and we have hostile forces on our current planet, finish our fight first.
                            if ( factionData.GetThreat( entity.Planet ).Actual > factionData.GetCap() * 100 && !highPriority )
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
                }
            }
            #endregion
            // If we have no raidStrength, stop here.
            if ( raidStrength.Count == 0 )
                return;

            #region Offensive Actions
            // Figure out the potential strength we would have to attack each planet.
            List<AttackAssessment> attackAssessments = new List<AttackAssessment>();
            foreach ( KeyValuePair<Planet, int> raidingPlanet in raidStrength )
            {
                raidingPlanet.Key.DoForLinkedNeighbors( delegate ( Planet adjPlanet )
                 {
                     // If friendly, skip.
                     if ( raidingPlanet.Key.GetControllingFaction().GetIsFriendlyTowards( adjPlanet.GetControllingFaction() ) )
                         return DelReturn.Continue;

                     // If we don't yet have an assessment for the planet, and  if it either has:
                     // Enough threat (based on the current AIP).
                     // Friendly forces and above 0 threat
                     // Add it
                     AttackAssessment adjAssessment = (from o in attackAssessments where o.Target == adjPlanet select o).FirstOrDefault();
                     if ( adjAssessment == null )
                         if ( factionData.GetThreat( adjPlanet ).Hostile >= factionData.GetCap() * 100
                           || factionData.GetThreat(adjPlanet).Friendly > 0 && factionData.GetThreat(adjPlanet).Hostile > 0)
                         {
                             adjAssessment = new AttackAssessment( adjPlanet, (int)(factionData.GetThreat( adjPlanet ).Actual * 1.5) );
                             attackAssessments.Add( adjAssessment );
                         }
                         else
                         {
                             return DelReturn.Continue;
                         }

                     // Add our current fleet strength to the attack budget.
                     adjAssessment.Attackers.Add( raidingPlanet.Key, raidingPlanet.Value );

                     return DelReturn.Continue;
                 } );
            }

            // Make sure our best target is first.
            attackAssessments.Sort();

            // Keep poising to attack as long as the target we're aiming for is weak to us.
            while ( attackAssessments.Count > 0 && attackAssessments[0].AttackPower > attackAssessments[0].StrengthRequired )
            {
                // Stop the attack if too many ships aren't ready, unless we're already attacking.
                int notReady = 0, ready = 0;
                bool alreadyAttacking = false;
                for ( int x = 0; x < factionData.MilitiaLeaders.Count; x++ )
                {
                    // Skip checks if we're already attacking.
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
                for ( int y = 1; y < attackAssessments.Count; y++ )
                {
                    foreach ( Planet planet in attackAssessments[0].Attackers.Keys )
                        if ( attackAssessments[y].Attackers.ContainsKey( planet ) )
                            attackAssessments[y].Attackers.Remove( planet );
                }
                attackAssessments.RemoveAt( 0 );
                attackAssessments.Sort();
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
                || tempData.ResourcePoints.Contains( entity.PrimaryKeyID )
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

            if ( factionData.ResourcePoints.Contains( entity.PrimaryKeyID ) )
                factionData.ResourcePoints.Remove( entity.PrimaryKeyID );

            // Save any changes.
            entity.PlanetFaction.Faction.SetCivilianFactionExt( factionData );
        }
    }
}