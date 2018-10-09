﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Concurrency.Utilities;
using Orleans;
using Orleans.Concurrency;

namespace Concurrency.Interface
{
    public interface IDeterministicTransactionCoordinator : IGrainWithGuidKey
    {
        /// <summary>
        /// Start the TC
        /// </summary>
        /// <remarks>
        /// This must be called before any other method.
        /// </remarks>
        Task StartAsync();

        /// <summary>
        /// Stop the TM
        /// </summary>
        Task StopAsync();


        /// <summary>
        /// Emit determinictic transactions received in the current batch;
        /// </summary>
        /// 
        [AlwaysInterleave]
        Task<TransactionContext> NewTransaction(Dictionary<Guid, Tuple<String,int>> grainAccessInformation);

        /// <summary>
        /// Return contetx for a non-determinictic transaction
        /// </summary>
        /// 
        [AlwaysInterleave]
        Task<TransactionContext> NewTransaction();

        /// <summary>
        /// Actors call this function to notify coordinator that a transaction has been completed locally. 
        /// </summary>
        [AlwaysInterleave]
        Task AckBatchCompletion(int bid, Guid executor_id);

        [AlwaysInterleave]
        Task<Boolean> checkBatchCompletion(TransactionContext context);

        Task resetTimer(int batchInterval);

        /// <summary>
        /// Start a new transaction.
        /// </summary>
        /// <param name="timeout">
        /// Transaction is automatically aborted if it does not complete within timeout
        /// </param>
        /// <returns>Id of the started transaction</returns>
        //long StartTransaction(TimeSpan timeout);

        /// <summary>
        /// Initiates Transaction Commit.
        /// </summary>
        /// <param name="transactionInfo"></param>
        /// <remarks>
        /// Use GetTransactionState to poll for the outcome.
        /// </remarks>
        /// <exception cref="OrleansTransactionAbortedException"></exception>
        //void CommitTransaction(TransactionInfo transactionInfo);

        /// <summary>
        /// Abort Transaction.
        /// </summary>
        /// <param name="transactionId"></param>
        /// <param name="reason"></param>
        /// <returns></returns>
        /// <remarks>
        /// If called after CommitTransaction was called for the transaction it will be ignored.
        /// </remarks>
        //void AbortTransaction(long transactionId, OrleansTransactionAbortedException reason);

        /// <summary>
        /// Get the state of the transaction.
        /// </summary>
        /// <param name="transactionId"></param>
        /// <param name="abortingException">If the transaction aborted, returns the exception that caused the abort</param>
        /// <returns>Transaction state</returns>
        //TransactionStatus GetTransactionStatus(long transactionId, out OrleansTransactionAbortedException abortingException);

        /// <summary>
        /// Return a safe TransactionId for read-only snapshot isolation.
        /// </summary>
        //long GetReadOnlyTransactionId();
    }
}
