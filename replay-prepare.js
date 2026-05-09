#!/usr/bin/env node
// Replay GSD tool calls to reconstruct the database.
// Outputs a Node script that calls each tool function with the exact original arguments.
// 
// This generates replay-execute.js which can be run to replay all calls.

const fs = require('fs');
const data = JSON.parse(fs.readFileSync('gsd-replay.json', 'utf8'));

const REPLAY_TOOLS = new Set([
  'gsd_summary_save',
  'gsd_plan_milestone', 
  'gsd_decision_save',
  'gsd_plan_slice',
  'gsd_complete_task',
  'gsd_task_complete',
  'gsd_requirement_update',
  'gsd_complete_slice',
  'gsd_checkpoint_db',
  'gsd_validate_milestone',
  'gsd_complete_milestone',
]);

const filtered = data.filter(c => REPLAY_TOOLS.has(c.tool));

// Generate a JSON file with just the replay data for the agent to execute
fs.writeFileSync('gsd-replay-calls.json', JSON.stringify(filtered, null, 2));

console.log(`Prepared ${filtered.length} calls for replay`);
console.log('Written to gsd-replay-calls.json');
