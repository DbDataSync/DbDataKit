using System.Text;
using DbDataKit.Apps;

namespace DbDataProxy.Server.Serving;

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
        var caller = new StoreCaller(call.SubjectId, call.SubjectName, IsAgent: false, call.Roles);
        var answer = call.Route switch
        {
            AppDataRoute.Row => await api.RowAsync(caller, call.Model, call.Entity!, call.Query, cancellationToken),
            AppDataRoute.Rows => await api.RowsAsync(caller, call.Model, call.Entity!, call.Query, cancellationToken),
            _ => await api.ChangesAsync(caller, call.Model, call.Body, entity => call.Allows(entity, write: true), cancellationToken),
        };
        return Answer(answer);
    }

    private static AppResponse Answer(StoreAnswer answer) =>
        new(answer.Status, "application/json; charset=utf-8", AppDataGateway.Cors, Encoding.UTF8.GetBytes(answer.Body.ToJsonString()));
}
