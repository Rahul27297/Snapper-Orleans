﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Concurrency.Interface;
using Concurrency.Interface.Nondeterministic;
using Concurrency.Interface.Logging;
using Utilities;
using System.Diagnostics;
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
        //protected Random rnd;
        private IGlobalTransactionCoordinatorGrain myCoordinator;
        private TransactionScheduler myScheduler;

        public TransactionExecutionGrain(String myUserClassName){
            
            this.myUserClassName = myUserClassName;
        }

        public async override Task OnActivateAsync()
        {            
            myPrimaryKey = this.GetPrimaryKey();
            var configTuple = await this.GrainFactory.GetGrain<IConfigurationManagerGrain>(Helper.convertUInt32ToGuid(0)).GetConfiguration(myUserClassName, myPrimaryKey);
            //Console.WriteLine($"Coordinator id = {configTuple.Item2}");
            myCoordinator = this.GrainFactory.GetGrain<IGlobalTransactionCoordinatorGrain>(Helper.convertUInt32ToGuid(configTuple.Item2));
            this.state = new HybridState<TState>(configTuple.Item1.nonDetCCConfiguration.nonDetConcurrencyManager);
            if(configTuple.Item1.logConfiguration.isLoggingEnabled)
            {
                log = new Simple2PCLoggingProtocol<TState>(this.GetType().ToString(), myPrimaryKey, configTuple.Item1.logConfiguration.loggingStorageWrapper);
            }
            batchScheduleMap = new Dictionary<int, DeterministicBatchSchedule>();
            myScheduler = new TransactionScheduler(batchScheduleMap);
            coordinatorMap = new Dictionary<int, Guid>();
            //return base.OnActivateAsync();
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
            if(context.transactionID == batchScheduleMap[context.batchID].getLastTransaction())
                myScheduler.ackBatchCommit(context.batchID);
            //Console.WriteLine($"Transaction {context.transactionID}: completed executing.\n");
            return t1.Result;
        }

        public async Task<FunctionResult> StartTransaction(String startFunction, FunctionInput functionCallInput)
        {
            FunctionResult result = null;
            TransactionContext context = null;
            Task<FunctionResult> t1 = null;
            Boolean canCommit = false;
            try
            {
                context = await myCoordinator.NewTransaction();
                myScheduler.ackBatchCommit(context.highestBatchIdCommitted);
                functionCallInput.context = context;
                context.coordinatorKey = this.myPrimaryKey;
                //Console.WriteLine($"Transaction {context.transactionID}: is started.\n");
                FunctionCall c1 = new FunctionCall(this.GetType(), startFunction, functionCallInput);
                t1 = this.Execute(c1);
                await t1;
                //Console.WriteLine($"Transaction {context.transactionID}: completed executing.\n");
                result = new FunctionResult(t1.Result.resultObject);
                canCommit = !t1.Result.hasException();
                
                //canCommit = canCommit & serializable;
                if (t1.Result.grainsInNestedFunctions.Count > 1 && canCommit)
                {
                    Boolean serializable = this.CheckSerializability(context.transactionID, t1.Result).Result;
                    canCommit = serializable;
                    if(canCommit)
                        canCommit = await Prepare_2PC(context.transactionID, myPrimaryKey, t1.Result);
                } else
                {
                    Debug.Assert(t1.Result.grainsInNestedFunctions.ContainsKey(myPrimaryKey) || !canCommit);
                }
                    
                if (canCommit)
                {
                    await Commit_2PC(context.transactionID, t1.Result);
                }
                else
                {
                    await Abort_2PC(context.transactionID, t1.Result);
                    result.setException();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"\n Exception(StartTransaction)::{this.myPrimaryKey}: transaction {startFunction} {context.transactionID} exception {e.Message}");
            }
            if(t1.Result.beforeSet.Count != 0)
            {
                if (t1.Result.grainWithHighestBeforeBid.Item1 == myPrimaryKey && t1.Result.grainWithHighestBeforeBid.Item2.Equals(this.myUserClassName))
                    await this.WaitForBatchCommit(t1.Result.maxBeforeBid);
                else
                    await this.GrainFactory.GetGrain<ITransactionExecutionGrain>(t1.Result.grainWithHighestBeforeBid.Item1, t1.Result.grainWithHighestBeforeBid.Item2).WaitForBatchCommit(t1.Result.maxBeforeBid);
            }            
            return result;
        }

        public async Task<Boolean> Prepare_2PC(int tid, Guid coordinatorKey, FunctionResult result)
        {
            Dictionary<Guid, String> grainIDsInTransaction = result.grainsInNestedFunctions;
            bool hasException = result.hasException();
            bool canCommit = !hasException;
            if (!hasException)
            {
                // Prepare Phase
                HashSet<Guid> participants = new HashSet<Guid>();
                participants.UnionWith(grainIDsInTransaction.Keys);
                Task logTask = Task.CompletedTask;
                if (log != null)
                    logTask = log.HandleBeforePrepareIn2PC(tid, coordinatorKey, participants);

                List<Task<Boolean>> prepareResult = new List<Task<Boolean>>();
                //Console.WriteLine($"Transaction {context.transactionID} send prepare messages to {grainIDsInTransaction.Count} grains. \n");
                foreach (var grain in grainIDsInTransaction)
                {
                    prepareResult.Add(this.GrainFactory.GetGrain<ITransactionExecutionGrain>(grain.Key, grain.Value).Prepare(tid));
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
            return canCommit;
        }

        public async Task<Boolean> CheckSerializability(int tid, FunctionResult result)
        {
            Boolean serializable = true;
            if (result.beforeSet.Count == 0)
            {
                //If before set is empty, the schedule must be serializable
                return true;
            }
            else if (result.isBeforeAfterConsecutive)
            {
                //The after set is complete
                if (result.maxBeforeBid < result.minAfterBid)
                    return true;
                else
                    serializable = false; //False Positive abort;
            }
            else
            {
                //The after set is not complete, there are holes between maxBeforeBid and minAfterBid
                if (result.beforeSet.Overlaps(result.afterSet))
                    serializable = false;
                else if (result.maxBeforeBid > result.minAfterBid)
                    serializable = false; //False Positive abort;
                else
                {
                    //Go to GC for complete after set;
                    //HashSet<int> completeAfterSet = await myCoordinator.GetCompleteAfterSet(tid, null);
                    serializable = false;
                }
            }
            return serializable;

        }

        public async Task Commit_2PC(int tid, FunctionResult result)
        {
            Dictionary<Guid, String> grainIDsInTransaction = result.grainsInNestedFunctions;
            List<Task> commitTasks = new List<Task>();
            if (log != null)
                commitTasks.Add(log.HandleOnCommitIn2PC(state, tid, coordinatorMap[tid]));
            //Console.WriteLine($"Transaction {context.transactionID}: prepared to commit. \n");
            foreach (var grain in grainIDsInTransaction)
            {
                commitTasks.Add(this.GrainFactory.GetGrain<ITransactionExecutionGrain>(grain.Key, grain.Value).Commit(tid));
            }
            await Task.WhenAll(commitTasks);
            //Console.WriteLine($"Transaction {context.transactionID}: committed. \n");
        }

        public async Task Abort_2PC(int tid, FunctionResult result)
        {
            Dictionary<Guid, String> grainIDsInTransaction = result.grainsInNestedFunctions;
            List<Task> abortTasks = new List<Task>();
            //Presume Abort
            //Console.WriteLine($"Transaction {context.transactionID}: prepared to abort. \n");
            foreach (var grain in grainIDsInTransaction)
            {
                abortTasks.Add(this.GrainFactory.GetGrain<ITransactionExecutionGrain>(grain.Key, grain.Value).Abort(tid));
            }
            await Task.WhenAll(abortTasks);
            //Console.WriteLine($"Transaction {context.transactionID}: aborted. \n");
        }

        /**
         * On receive the schedule for a specific batch
         * 1. Store this schedule.
         * 2. Check if there is function call that should be executed now, and execute it if yes.
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

                //Update before set and after set
                int tid = call.funcInput.context.transactionID;
                updateExecutionResult(tid, invokeRet);
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

                    var coordinator = this.GrainFactory.GetGrain<IGlobalTransactionCoordinatorGrain>(batchScheduleMap[bid].globalCoordinator);
                    Task ack = coordinator.AckBatchCompletion(bid, myPrimaryKey);
                }
                return ret;
                //XXX: Check if this works -> return new FunctionResult(ret);
            }
        }

        //Update the metadata of the execution results, including accessed grains, before/after set, etc.
        public void updateExecutionResult(int tid, FunctionResult invokeRet)
        {
            if (invokeRet.grainWithHighestBeforeBid == null)
                invokeRet.grainWithHighestBeforeBid = new Tuple<Guid, string>(this.myPrimaryKey, this.myUserClassName);

            int maxBeforeBid, minAfterBid;
            bool isBeforeAfterConsecutive = false;

            if (!invokeRet.grainsInNestedFunctions.ContainsKey(this.myPrimaryKey))
                invokeRet.grainsInNestedFunctions.Add(myPrimaryKey, myUserClassName);

            var beforeSet = myScheduler.getBeforeSet(tid, out maxBeforeBid);
            var afterSet = myScheduler.getAfterSet(tid, out minAfterBid);
            invokeRet.beforeSet.UnionWith(beforeSet);
            invokeRet.afterSet.UnionWith(afterSet);
            if (maxBeforeBid == int.MinValue || minAfterBid == int.MaxValue)
                isBeforeAfterConsecutive = false;
            else if (batchScheduleMap[minAfterBid].lastBatchID == maxBeforeBid)
                isBeforeAfterConsecutive = true;
            invokeRet.setSchedulingStatistics(maxBeforeBid, minAfterBid, isBeforeAfterConsecutive, new Tuple<Guid, string>(this.myPrimaryKey, this.myUserClassName));
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

        public async Task WaitForBatchCommit(int bid)
        {
            await myScheduler.waitForBatchCommit(bid);
        }
    }
}
