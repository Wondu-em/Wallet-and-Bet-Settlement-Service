
namespace Wallet.Infrastructure.Persistence;

/// <summary>
/// Database-level guarantees for the ledger. Called from the "LedgerGuards" migration.
///  1. Append-only tables: UPDATE / DELETE / TRUNCATE raise an exception.
///  2. Every ledger transaction must balance (debits = credits), checked at COMMIT.
/// </summary>
public static class LedgerGuardsSql
{

    
    public const string Up = """
CREATE OR REPLACE FUNCTION forbid_mutation() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'Table "%" is append-only (% is not allowed)', TG_TABLE_NAME, TG_OP
        USING ERRCODE = 'restrict_violation';
END;
$$ LANGUAGE plpgsql;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['ledger_entries','ledger_transactions','event_status_history','bet_status_history','audit_log']
    LOOP
        EXECUTE format('CREATE TRIGGER trg_%1$s_no_update_delete BEFORE UPDATE OR DELETE ON %1$I FOR EACH ROW EXECUTE FUNCTION forbid_mutation()', t);
        EXECUTE format('CREATE TRIGGER trg_%1$s_no_truncate BEFORE TRUNCATE ON %1$I FOR EACH STATEMENT EXECUTE FUNCTION forbid_mutation()', t);
    END LOOP;
END $$;

CREATE OR REPLACE FUNCTION assert_ledger_tx_balanced() RETURNS trigger AS $$
DECLARE
    d numeric;
    c numeric;
BEGIN
    SELECT COALESCE(SUM(amount) FILTER (WHERE direction = 'Debit'), 0),
           COALESCE(SUM(amount) FILTER (WHERE direction = 'Credit'), 0)
      INTO d, c
      FROM ledger_entries
     WHERE transaction_id = NEW.transaction_id;

    IF d <> c THEN
        RAISE EXCEPTION 'Ledger transaction % is not balanced (debits %, credits %)', NEW.transaction_id, d, c
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE CONSTRAINT TRIGGER trg_ledger_entries_balanced
    AFTER INSERT ON ledger_entries
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION assert_ledger_tx_balanced();
""";

    public const string Down = """
DROP TRIGGER IF EXISTS trg_ledger_entries_balanced ON ledger_entries;
DROP FUNCTION IF EXISTS assert_ledger_tx_balanced();

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['ledger_entries','ledger_transactions','event_status_history','bet_status_history','audit_log']
    LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_%1$s_no_update_delete ON %1$I', t);
        EXECUTE format('DROP TRIGGER IF EXISTS trg_%1$s_no_truncate ON %1$I', t);
    END LOOP;
END $$;

DROP FUNCTION IF EXISTS forbid_mutation();
""";
}
