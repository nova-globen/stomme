using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Mediator;
using [Owner].Module.[Module].Application;
using [Owner].Module.[Module].Models;

namespace [Owner].Module.[Module].Services
{
    /// <summary>
    /// The service on the SERVER (a Blazor Server page, a prerender): the same requests the API sends, sent directly - so the
    /// keys in their <c>[Requires]</c> apply exactly as over HTTP.
    /// </summary>
    public sealed class Server[Module]Service(IMediator mediator) : I[Module]Service
    {
        public async Task<List<[Module]EntryModel>> ListEntriesAsync() => (await mediator.Send(new List[Module]EntriesQuery())).ToList();

        public Task<[Module]EntryModel> AddEntryAsync(Add[Module]EntryRequest request) => mediator.Send(new Add[Module]EntryCommand(request.Text));
    }
}
