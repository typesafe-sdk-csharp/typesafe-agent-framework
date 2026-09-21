using TypeSafe.AI;
using TypeSafe.AI.AgentFramework;

namespace TypeSafe.Sample.ChatWithSql;

public sealed class SqlSchemaContextProvider(ITypeSafeClient client) : TypeSafeAIContextProvider(
    client,
    q => q.Noul("customers_relevant", "Does this request need customer names?"),
    instructionFormatter: evaluation => evaluation.Nouls["customers_relevant"].Noul >= 0.50
        ? "Available schema: customers(id integer, name text). Return at most 5 rows."
        : "No relevant schema was selected. Ask for a customer-name query.");
