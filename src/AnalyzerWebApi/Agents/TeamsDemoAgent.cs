using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;

namespace AnalyzerWebApi.Agents
{
    public class TeamsDemoAgent : AgentApplication
    {
        public TeamsDemoAgent(AgentApplicationOptions options) : base(options)
        {
            OnActivity(ActivityTypes.Message, RepondreAsync, rank: RouteRank.Last);
        }

        private async Task RepondreAsync(ITurnContext contexte,ITurnState etat,CancellationToken cancellationToken)
        {
            await contexte.SendActivityAsync(MessageFactory.Text("Bonjour ! La connexion Teams fonctionne."),
                                            cancellationToken: cancellationToken);
        }
    }
}
