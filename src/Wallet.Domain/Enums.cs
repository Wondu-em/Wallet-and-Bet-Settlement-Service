namespace Wallet.Domain;

public enum UserRole { User, Admin }
public enum AccountType { UserWallet, EventEscrow, House, ExternalClearing }
public enum EntryDirection { Debit, Credit }
public enum LedgerTransactionType { Deposit, Withdraw, BetStake, Payout, Refund, Sweep }
public enum EventStatus { Open, Closed, Settled, Voided }
public enum BetStatus { Placed, Won, Lost, Refunded }
public enum ProviderDepositStatus { Pending, Confirmed, Failed }
public enum IdempotencyStatus { InProgress, Completed }
