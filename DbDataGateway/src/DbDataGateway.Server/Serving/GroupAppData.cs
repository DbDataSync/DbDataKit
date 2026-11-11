using System.Text;
using DbDataKit.Apps;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// An app's data calls answered by the group (plan 20 O08c): the apps host checked the app's token, and here the call goes to <see cref="GroupStoreApi"/> in the same process, as the person the token names with the roles the
/// identity home gave them on the app's instance, and only for what the manifest's <c>models</c> give the app: a read of an entity it lists, a change to one it may write. The group serves a model's default copy; an
/// instance bound to another copy is refused until the group serves models with copies.
/// </summary>
public sealed class GroupAppData(GroupStoreApi api) : IAppData
{
    public async Task<AppResponse> SendAsync(AppDataCall call, CancellationToken cancellationToken)
    {
        if (call.Copy is { } copy && copy != GroupStoreApi.DefaultCopy)
            return Answer(StoreAnswer.Error(404, $"The group serves the model's default copy, and this app is bound to '{copy}'; bind it to '{GroupStoreApi.DefaultCopy}', or leave the binding out."));
        if (call.Entity is { } entity && !call.Allows(entity, write: false))
            return Answer(StoreAnswer.Error(403, $"This app may not read `{entity}`: its manifest does not list that entity of '{call.Model}'."));
        // the schema is asked of a model as a whole: the scope is asked before the group, so nothing says whether a model the manifest does not list exists
        if (call.Route == AppDataRoute.Schema && !call.Scope.Any(entry => entry.StartsWith(call.Model + "/", StringComparison.Ordinal)))
            return Answer(StoreAnswer.Error(403, $"This app may not read '{call.Model}': its manifest does not list the model."));
        var caller = new StoreCaller(call.SubjectId, call.SubjectName, IsAgent: false, call.Roles, call.Via);
        var answer = call.Route switch
        {
            AppDataRoute.Row => await api.RowAsync(caller, call.Model, call.Entity!, call.Query, cancellationToken),
            AppDataRoute.Rows => await api.RowsAsync(caller, call.Model, call.Entity!, call.Query, cancellationToken),
            AppDataRoute.Schema => api.Schema(caller, call.Model, entity => call.Allows(entity, write: false)),
            _ => await api.ChangesAsync(caller, call.Model, call.Body, call.Proposes, cancellationToken, mayApply: entity => call.Allows(entity, write: true)),
        };
        return Answer(answer);
    }

    private static AppResponse Answer(StoreAnswer answer) =>
        new(answer.Status, "application/json; charset=utf-8", AppDataGateway.Cors, Encoding.UTF8.GetBytes(answer.Body.ToJsonString()));
}
