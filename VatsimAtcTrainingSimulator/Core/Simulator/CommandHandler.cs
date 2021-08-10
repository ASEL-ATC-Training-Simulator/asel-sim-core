﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VatsimAtcTrainingSimulator.Core.Simulator
{
    public static class CommandHandler
    {
        private static Queue<IAircraftCommand> commandQueue = new Queue<IAircraftCommand>();
        private static object commandQueueLock = new object();
        private static bool processingCommand = false;

        private static void ProcessNextCommand()
        {
            if (processingCommand)
            {
                return;
            }

            processingCommand = true;
            while (commandQueue.Count > 0)
            {
                IAircraftCommand cmd;
                lock (commandQueueLock)
                {
                    cmd = commandQueue.Dequeue();
                }

                // Generate random delay
                int delay = new Random().Next(0, 3000);
                Thread.Sleep(delay);

                cmd.ExecuteCommand();
            }
            processingCommand = false;
        }

        public static List<string> HandleCommand(string commandName, VatsimClientPilot aircraft, List<string> args, Action<string> logger)
        {
            string cmdNameNormalized = commandName.ToLower();
            IAircraftCommand cmd;

            // Get Command
            if (cmdNameNormalized.Equals("fh"))
            {
                cmd = new FlyHeadingCommand();
            } else if (cmdNameNormalized.Equals("tl"))
            {
                cmd = new TurnLeftHeadingCommand();
            } else if (cmdNameNormalized.Equals("tr")){
                cmd = new TurnRightHeadingCommand();
            } else if (cmdNameNormalized.Equals("speed") || cmdNameNormalized.Equals("spd"))
            {
                cmd = new SpeedCommand();
            } else
            {
                logger($"ERROR: Command {commandName} not valid!");
                return args;
            }

            // Get new args after processing command
            cmd.Aircraft = aircraft;
            cmd.Logger = logger;

            // Make sure command is valid before running.
            if (cmd.HandleCommand(ref args))
            {
                // Add to Queue
                lock (commandQueueLock)
                {
                    commandQueue.Enqueue(cmd);
                }

                // Launch thread to execute queue
                Thread t = new Thread(ProcessNextCommand);
                t.Start();
            }

            // Return args
            return args;
        }
    }
}
