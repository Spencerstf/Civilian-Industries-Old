using Arcen.Universal;
using Arcen.AIW2.Core;
using Arcen.AIW2.External;

namespace Arcen.AIW2.SK
{
    public class VirionSquadDataExternalData : IArcenExternalDataPatternImplementation
    {
        // Make sure you use the same class name that you use for whatever data you want saved here.
        private VirionSquadData Data;

        public static int PatternIndex;

        // So this is essentially what type of thing we're going to 'attach' our class to.
        public static string RelatedParentTypeName = "GameEntity_Squad";

        public void ReceivePatternIndex( int Index )
        {
            PatternIndex = Index;
        }
        public int GetNumberOfItems()
        {
            return 1;
        }
        public bool GetShouldInitializeOn( string ParentTypeName )
        {
            // Figure out which object type has this sort of ExternalData (in this case, Faction)
            return ArcenStrings.Equals( ParentTypeName, RelatedParentTypeName );
        }

        public void InitializeData( object ParentObject, object[] Target )
        {
            this.Data = new VirionSquadData();
            Target[0] = this.Data;
        }
        public void SerializeExternalData( object[] Source, ArcenSerializationBuffer Buffer )
        {
            //For saving to disk, translate this object into the buffer
            VirionSquadData data = (VirionSquadData)Source[0];
            data.SerializeTo( Buffer );
        }
        public void DeserializeExternalData( object ParentObject, object[] Target, int ItemsToExpect, ArcenDeserializationBuffer Buffer )
        {
            //reverses SerializeData; gets the date out of the buffer and populates the variables
            Target[0] = new VirionSquadData( Buffer );
        }
    }

    // The following is a helper function to the above, designed to allow us to save and load data on demand.
    public static class ExtensionMethodsFor_VirionSquadData
    {
        // This loads the data assigned to whatever ParentObject you pass. So, say, you could assign the same class to different ships, and each would be able to get back the values assigned to it.
        // In our specific case here, we're going to be assigning a dictionary to every faction.
        public static VirionSquadData GetVirionSquadDataExt( this GameEntity_Squad ParentObject )
        {
            return (VirionSquadData)ParentObject.ExternalData.GetCollectionByPatternIndex( (int)VirionSquadDataExternalData.PatternIndex ).Data[0];
        }
        // This meanwhile saves the data, assigning it to whatever ParentObject you pass.
        public static void SetVirionSquadDataExt( this GameEntity_Squad ParentObject, VirionSquadData data )
        {
            ParentObject.ExternalData.GetCollectionByPatternIndex( (int)VirionSquadDataExternalData.PatternIndex ).Data[0] = data;
        }
    }
}
