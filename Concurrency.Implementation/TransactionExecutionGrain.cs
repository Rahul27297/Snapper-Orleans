﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Concurrency.Interface;
using Concurrency.Interface.Nondeterministic;
using Concurrency.Interface.Logging;
using Utilities;
using Concurrency.Implementation.Logging;
using Orleans;

namespace Concurrency.Implementation
{
    public abstract class TransactionExecutionGrain<TState> : Grain, ITransactionExecutionGrain where TState : ICloneable, new()
    {   
        private Dictionary<int, DeterministicBatchSchedule> batchScheduleMap;        
        private Dictionary<int, Guid> coordinatorMap;                
        protected Guid myPrimaryKey;
        protected ITransactionalState<TState> state;
        protected ILoggingProtocol<TState> log = null;
        protected String myUserClassName;
        protected int numCoordinators = 10;
        protected Random rnd;
        private IGlobalTransactionCoordinator myCoordinator;
        private TransactionScheduler myScheduler;

        public TransactionExecutionGrain(TState state, String myUserClassName){
            this.state = new HybridState<TState>(state);
            this.myUserClassName = myUserClassName;
        }

        public TransactionExecutionGrain()
        {

        }
        public override Task OnActivateAsync()
        {
            rnd = new Random();
            myCoordinator = this.GrainFactory.GetGrain<IGlobalTransactionCoordinator>(Helper.convertUInt32ToGuid((UInt32)rnd.Next(0, numCoordinators)));            
            batchScheduleMap = new Dictionary<int, DeterministicBatchSchedule>();            
            myPrimaryKey = this.GetPrimaryKey();
            myScheduler = new TransactionScheduler(batchScheduleMap);
            //Enable the following line for logging
            //log = new Simple2PCLoggingProtocol<TState>(this.GetType().ToString(), myPrimaryKey);
            coordinatorMap = new Dictionary<int, Guid>();
            return base.OnActivateAsync();
        }


        /**
         * Submit a determinictic transaction to the coordinator. 
         * On receiving the returned transaction context, start the execution of a transaction.
         * 
         */
        public async Task<FunctionResult> StartTransaction(Dictionary<Guid, Tuple<String,int>> grainAccessInformation, String startFunction, FunctionInput inputs)
        {
            
            TransactionContext context = await myCoordinator.NewTransaction(grainAccessInformation);
            inputs.context = context;
            FunctionCall c1 = new FunctionCall(this.GetType(), startFunction, inputs);
            Task<FunctionResult> t1 = this.Execute(c1);
            Task t2 = myCoordinator.checkBatchCompletion(context);
            await Task.WhenAll(t1, t2);
            //Console.WriteLine($"Transaction {context.transactionID}: completed executing.\n");
            return t1.Result;
        }

        public async Task<FunctionResult> StartTransaction(String startFunction, FunctionInput functionCallInput)
        {
            FunctionResult result = null;
            TransactionContext context = null;
            try
            {
                context = await myCoordinator.NewTransaction();
                functionCallInput.context = context;
                context.coordinatorKey = this.myPrimaryKey;
                //Console.WriteLine($"Transaction {context.transactionID}: is started.\n");
                FunctionCall c1 = new FunctionCall(this.GetType(), startFunction, functionCallInput);
                Task<FunctionResult> t1 = this.Execute(c1);
                await t1;
                //Console.WriteLine($"Transaction {context.transactionID}: completed executing.\n");
                Dictionary<Guid, String> grainIDsInTransaction = t1.Result.grainsInNestedFunctions;
                result = new FunctionResult(t1.Result.resultObject);
                bool hasException = t1.Result.hasException();
                bool canCommit = !hasException;
                if (!hasException)
                {
                    // Prepare Phase
                    HashSet<Guid> participants = new HashSet<Guid>();
                    participants.UnionWith(grainIDsInTransaction.Keys);
                    Task logTask = Task.CompletedTask;
                    if (log != null)
                        logTask = log.HandleBeforePrepareIn2PC(context.transactionID, context.coordinatorKey, participants);

                    List<Task<Boolean>> prepareResult = new List<Task<Boolean>>();

                    Console.WriteLine($"Transaction {context.transactionID} send prepare messages to {grainIDsInTransaction.Count} grains. \n");
                    foreach (var grain in grainIDsInTransaction)
                    {
                        prepareResult.Add(this.GrainFactory.GetGrain<ITransactionExecutionGrain>(grain.Key, grain.Value).Prepare(context.transactionID));
                    }
                    await Task.WhenAll(logTask, Task.WhenAll(prepareResult));

                    foreach (Task<Boolean> vote in prepareResult)
                    {
                        if (vote.Result == false)
                        {
                            canCommit = false;
                            break;
                        }
                    }
                }

                // Commit / Abort Phase
                List<Task> commitTasks = new List<Task>();
                List<Task> abortTasks = new List<Task>();
                if (canCommit)
                {
                    if (log != null)
                        commitTasks.Add(log.HandleOnCommitIn2PC(state, context.transactionID, coordinatorMap[context.transactionID]));
                    //Console.WriteLine($"Transaction {context.transactionID}: prepared to commit. \n");
                    foreach (var grain in grainIDsInTransaction)
                    {
                        commitTasks.Add(this.GrainFactory.GetGrain<ITransactionExecutionGrain>(grain.Key, grain.Value).Commit(context.transactionID));
                    }

                    await Task.WhenAll(commitTasks);
                    //Console.WriteLine($"Transaction {context.transactionID}: committed. \n");
                }
                else
                {
                    //Presume Abort
                    //if(log != null)
                    //abortTasks.Add(log.HandleOnAbortIn2PC(state, context.transactionID, coordinatorMap[context.transactionID]));

                    //Console.WriteLine($"Transaction {context.transactionID}: prepared to abort. \n");
                    foreach (var grain in grainIDsInTransaction)
                    {
                        abortTasks.Add(this.GrainFactory.GetGrain<ITransactionExecutionGrain>(grain.Key, grain.Value).Abort(context.transactionID));
                    }
                    await Task.WhenAll(abortTasks);
                    //Console.WriteLine($"Transaction {context.transactionID}: aborted. \n");
                    //Ensure the exception is set if the voting phase decides to abort
                    result.setException();
                }
                //myScheduler.ackComplete(context.transactionID);
            } catch (Exception e)
            {
                Console.WriteLine($"\n Exception(StartTransaction)::{this.myPrimaryKey}: transaction {context.transactionID} exception {e.Message}");
                
            }
            return result;
        }

        /**
         * On receive the schedule for a specific batch
         * 1. Store this schedule.
         * 2. Check if there is function call that should be executed now, and execute it if yes.
         *
         */
         
        public Task ReceiveBatchSchedule(DeterministicBatchSchedule schedule)
        {
            //Console.WriteLine($"\n {this.myPrimaryKey}: Received schedule for batch {schedule.batchID}, the previous batch is {schedule.lastBatchID}");        
            batchScheduleMap.Add(schedule.batchID, schedule);            
            myScheduler.RegisterDeterministicBatchSchedule(schedule.batchID);
            return Task.CompletedTask;
        }

        /**
         *Allow reentrance to enforce ordered execution
         */
        public async Task<FunctionResult> Execute(FunctionCall call)
        {

            if (call.funcInput.context.isDeterministic == false)
            {//Non-deterministic exection
                FunctionResult invokeRet = null;
                try
                {
                    await myScheduler.waitForTurn(call.funcInput.context.transactionID);
                    invokeRet = await InvokeFunction(call);
                }
                catch(Exception e)
                {
                    Console.WriteLine($"\n Exception::InvokeFunction: {e.Message.ToString()}");
                }
                if(!invokeRet.grainsInNestedFunctions.ContainsKey(this.myPrimaryKey))
                    invokeRet.grainsInNestedFunctions.Add(myPrimaryKey, myUserClassName);
                return invokeRet;
            }
            else
            {
                int tid = call.funcInput.context.transactionID;
                int bid = call.funcInput.context.batchID;
                var myTurnIndex = await myScheduler.waitForTurn(bid, tid);
                //Execute the function call;
                var ret = await InvokeFunction(call);
                if (myScheduler.ackComplete(bid, tid, myTurnIndex))
                {
                    //The scheduler has switched batches, need to commit now
                    if (log != null && state != null)
                        await log.HandleOnCompleteInDeterministicProtocol(state, bid, batchScheduleMap[bid].globalCoordinator);

                    var coordinator = this.GrainFactory.GetGrain<IGlobalTransactionCoordinator>(batchScheduleMap[bid].globalCoordinator);
                    Task ack = coordinator.AckBatchCompletion(bid, myPrimaryKey);
                }
                return ret;
                //XXX: Check if this works -> return new FunctionResult(ret);
            }
        }

        public async Task<FunctionResult> InvokeFunction(FunctionCall call)
        {
            var context = call.funcInput.context;
            var key = (context.isDeterministic) ? context.batchID : context.transactionID;            
            if(!coordinatorMap.ContainsKey(key))
            {
                coordinatorMap.Add(key, context.coordinatorKey);
            }
            FunctionInput functionCallInput = call.funcInput;                        
            MethodInfo mi = call.type.GetMethod(call.func);
            Task<FunctionResult> t = (Task<FunctionResult>)mi.Invoke(this, new object[] { functionCallInput });
            await t;
            return t.Result;
        }

        private void Cleanup(int tid)
        {
            coordinatorMap.Remove(tid);
        }

        public async Task Abort(int tid)
        {
            //Console.WriteLine($"\n\n Grain {this.myPrimaryKey}: receives Abort message for transaction {tid}. \n\n");
            if (state == null)
                return;

            var tasks = new List<Task>();
            tasks.Add(this.state.Abort(tid));

            //Presume Abort
            //if (log != null)
            //tasks.Add(log.HandleOnAbortIn2PC(state, tid, coordinatorMap[tid]));
            myScheduler.ackComplete((int)tid);
            Cleanup(tid);
            await Task.WhenAll(tasks);
        }

        public async Task Commit(int tid)
        {
            //Console.WriteLine($"\n\n Grain {this.myPrimaryKey}: receives Commit message for transaction {tid}. \n\n");
            if (state == null)
                return;


            var tasks = new List<Task>();
            tasks.Add(this.state.Commit(tid));
            if (log != null)
                tasks.Add(log.HandleOnCommitIn2PC(state, tid, coordinatorMap[tid]));
            myScheduler.ackComplete((int)tid);
            Cleanup(tid);
            await Task.WhenAll(tasks);
        }

        /**
         * Stateless grain always vote "yes" for 2PC.
         */
        public async Task<bool> Prepare(int tid)
        {
            //Console.WriteLine($"\n\n Grain {this.myPrimaryKey}: receives Prepare message for transaction {tid}. \n\n");

            if (state == null)
                return true;

            var prepareResult = await this.state.Prepare(tid);
            if(prepareResult && log != null)
            if (log != null)            
                await log.HandleOnPrepareIn2PC(state, tid, coordinatorMap[tid]);
            return prepareResult;
        }
    }
}
