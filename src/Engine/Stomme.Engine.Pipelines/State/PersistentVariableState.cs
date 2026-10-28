// using System.Text.Json.Serialization;
//
// namespace Stomme.Engine.Pipelines.State;
//
// public class PersistentVariableState
// {
//     [JsonConstructor]
//     public PersistentVariableState()
//     {
//     }
//
//     public PersistentVariableState(string name, string storageDriverId)
//     {
//         Name = name;
//         StorageDriverId = storageDriverId;
//     }
//
//     public string Name { get; set; } = default!;
//     public string StorageDriverId { get; set; } = default!;
// }