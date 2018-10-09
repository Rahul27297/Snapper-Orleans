﻿using Concurrency.Interface.Nondeterministic;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Utilities;

namespace Concurrency.Implementation.Nondeterministic
{
    public class TimestampTransactionalState<TState> : ITransactionalState<TState> where TState : ICloneable, new()
    {

        private TState commitedState;
        private long readTs;
        private long writeTs;
        private long commitTransactionId;

        //The dependancy list of transactions, node_{i} depends on node_{i-1}.
        private DLinkedList<TransactionStateInfo> transactionList;
        //Key: transaction id
        private Dictionary<long, Node<TransactionStateInfo>> transactionMap;

        public TimestampTransactionalState()
        {
            commitedState = new TState();
            readTs = -1;
            writeTs = -1;
            commitTransactionId = -1;

            transactionList = new DLinkedList<TransactionStateInfo>();
            transactionMap = new Dictionary<long, Node<TransactionStateInfo>>();
        }

        public TimestampTransactionalState(TState s)
        {
            commitedState = s;
            readTs = -1;
            writeTs = -1;
            commitTransactionId = -1;

            transactionList = new DLinkedList<TransactionStateInfo>();
            transactionMap = new Dictionary<long, Node<TransactionStateInfo>>();

        }
        public Task<TState> ReadWrite(long tid)
        {
            long rts, wts, depTid;
            TState state;

            if (transactionList.size == 0)
            {
                state = commitedState;
                rts = readTs;
                wts = writeTs;
                depTid = commitTransactionId;
            }
            else
            {
                Node<TransactionStateInfo> lastNode = transactionList.tail;
                while(lastNode != null)
                {
                    if (lastNode.data.status.Equals(Status.Aborted))
                        lastNode = lastNode.prev;
                    else
                        break;
                }
                if(lastNode != null)
                {
                    TransactionStateInfo dependState = lastNode.data;
                    state = dependState.state;
                    rts = dependState.rts;
                    wts = dependState.wts;
                    depTid = dependState.tid;
                }
                else
                {
                    state = commitedState;
                    rts = readTs;
                    wts = writeTs;
                    depTid = commitTransactionId;
                }
            }

            //check read timestamp
            if (tid < wts)
            {
                transactionMap.Add(tid, new Node<TransactionStateInfo>(new TransactionStateInfo(tid, Status.Aborted)));
                throw new Exception($"Read: Transaction {tid} is aborted as its timestamp is smaller than write timestamp {wts}.");
            }

            //update read timestamp;
            rts = Math.Max(rts, tid);

            //check write timestamp
            if (tid < rts)
            {
                transactionMap.Add(tid, new Node<TransactionStateInfo>(new TransactionStateInfo(tid, Status.Aborted)));
                throw new Exception($"Write: Transaction {tid} is aborted as its timestamp is smaller than read timestamp {rts}.");
            }

            //Clone the state of the depending transaction
            TState copy = (TState)state.Clone();
            TransactionStateInfo info = new TransactionStateInfo(tid, depTid, rts, tid, Status.Executing, copy);

            //Update the transaction table and dependency list
            Node<TransactionStateInfo> node = transactionList.Append(info);
            transactionMap.Add(tid, node);

            //Should we return a copy of copy, as we don't wanna user to update this state
            return Task.FromResult<TState>(copy);
        }

        public async Task<bool> Prepare(long tid)
        {
            //Console.WriteLine($"\n\n Received prepare message of transaction {tid} \n\n");
            //Console.WriteLine($"Transaction {tid}: Grain: Prepare: {transactionMap.ContainsKey(tid)} \n");
            if (transactionMap[tid].data.status.Equals(Status.Aborted))
                return false;
            else
            {
                //Vote "yes" if it depends commited state.
                long depTid = transactionMap[tid].data.depTid;
                if (depTid <= this.commitTransactionId)
                    return true;
                else
                {
                    Console.WriteLine($" State Prepare: {tid} depends on {depTid}: {transactionMap.ContainsKey(depTid)}\n");
                    TransactionStateInfo depTxInfo = transactionMap[depTid].data;
                    //wait until its dependent transaction is committed or aborted.
                    if (depTxInfo.ExecutionPromise.Task.IsCompleted)
                    {
                        if (depTxInfo.status.Equals(Status.Committed))
                            return true;
                        else if (depTxInfo.status.Equals(Status.Aborted))
                        {
                            Console.WriteLine($"State: Transaction {tid}: aborts as it depends on {depTxInfo.tid}. \n");
                            return false;
                        }
                    }
                    else
                    {
                        //Console.WriteLine($"Transaction {tid}: is waiting promise of previous transaction {depTxInfo.tid}. \n");
                        await depTxInfo.ExecutionPromise.Task;
                        if (depTxInfo.status.Equals(Status.Committed))
                            return true;
                        else if (depTxInfo.status.Equals(Status.Aborted))
                        {
                            Console.WriteLine($"State: Transaction {tid}: aborts as it depends on {depTxInfo.tid}. \n");
                            return false;
                        }
                    }
                }
            }
            //by default abort the transaction ???
            return false;
        }

        //Clear committed/aborted transactions before "node".
        private void CleanUp(Node<TransactionStateInfo> node)
        {
            Node<TransactionStateInfo> curNode = this.transactionList.head;
            while (curNode != null)
            {
                if (curNode.data.tid == node.data.tid)
                    return;
                if (curNode.data.tid < node.data.tid &&(curNode.data.status.Equals(Status.Aborted) || curNode.data.status.Equals(Status.Committed))) {
                    transactionList.Remove(curNode);
                    transactionMap.Remove(curNode.data.tid);
                 }
                curNode = curNode.next;
            }
        }

        public Task Commit(long tid)
        {
            //Update status and Set the execution promise, such that the blocking prepare() of the dependant transactions can proceed.
            //Console.WriteLine($"Transaction {tid}: Grain: Commit: {transactionMap.ContainsKey(tid)} \n");
            Node<TransactionStateInfo> node = transactionMap[tid];
            node.data.status = Status.Committed;
            node.data.ExecutionPromise.SetResult(true);
            //Console.WriteLine($"\n\n Set the promise of transaction {tid}.\n\n");
            //Remove transactions that are prior to this one

            CleanUp(node);

            this.commitTransactionId = tid;
            this.commitedState = node.data.state;
            this.readTs = node.data.rts;
            this.writeTs = node.data.wts;
            return Task.CompletedTask;
        }

        public Task Abort(long tid)
        {
            //Console.WriteLine($"Transaction {tid}: Grain: Abort: {transactionMap.ContainsKey(tid)} \n");
            //Update status and Set the execution promise, such that the blocking prepare() of the dependant transactions can proceed.
            Node<TransactionStateInfo> node = transactionMap[tid];
            node.data.status = Status.Aborted;
            node.data.ExecutionPromise.SetResult(true);

            //Remove transactions that are prior to this one
            CleanUp(node);

            return Task.CompletedTask;
        }

        public Task<TState> Read(long tid)
        {

            //Should we return a copy of copy, as we don't wanna user to update this state
            return Task.FromResult<TState>(this.commitedState);
        }
        public Task Write(long tid)
        {
            return Task.CompletedTask;
        }

        public enum Status
        {
            Executing,
            Prepared,
            Aborted,
            Committed
        }

        private class TransactionStateInfo
        {

            public long tid { get; set; }
            public Status status { get; set; }

            public TState state { get; set; }
            public long rts { get; set; }
            public long wts { get; set; }

            public long depTid { get; set; }

            public TaskCompletionSource<Boolean> ExecutionPromise { get; set; }

            public TransactionStateInfo(long tid, long depTid, long rts, long wts, Status status, TState copy)
            {
                this.tid = tid;
                this.depTid = depTid;
                this.status = status;
                this.state = copy;
                this.rts = rts;
                this.wts = wts;
                ExecutionPromise = new TaskCompletionSource<Boolean>();
            }

            public TransactionStateInfo(long tid, Status status)
            {
                this.tid = tid;
                this.status = status;
                ExecutionPromise = new TaskCompletionSource<Boolean>();
            }

        }

        private class LogRecord<T>
        {
            public T NewVal { get; set; }
        }

    }
}
