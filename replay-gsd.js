#!/usr/bin/env node
// Replay GSD tool calls from extracted JSON to reconstruct the database.
// This script outputs a shell script that calls each GSD tool via the pi CLI.
// 
// Usage: node replay-gsd.js > replay-plan.txt
// Then execute the calls manually or via the agent.

const fs = require('fs');
const data = JSON.parse(fs.readFileSync('gsd-replay.json', 'utf8'));

// Filter to only calls that modify state (skip status checks, journal queries, exec)
const REPLAY_TOOLS = [
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
];

const filtered = data.filter(c => REPLAY_TOOLS.includes(c.tool));

console.log(`Total calls: ${data.length}`);
console.log(`Replayable calls: ${filtered.length}`);
console.log('---');

for (let i = 0; i < filtered.length; i++) {
  const c = filtered[i];
  const a = c.args;
  let summary = `[${i}/${filtered.length}] ${c.tool}`;
  
  if (c.tool === 'gsd_summary_save') {
    summary += ` | ${a.artifact_type} | ${(a.content || '').length} chars`;
  } else if (c.tool === 'gsd_plan_milestone') {
    summary += ` | ${a.milestoneId} | ${a.title} | ${(a.slices || []).length} slices`;
  } else if (c.tool === 'gsd_decision_save') {
    summary += ` | ${(a.decision || '').substring(0, 80)}`;
  } else if (c.tool === 'gsd_plan_slice') {
    summary += ` | ${a.milestoneId}/${a.sliceId} | ${(a.goal || '').substring(0, 60)}`;
  } else if (c.tool === 'gsd_complete_task' || c.tool === 'gsd_task_complete') {
    summary += ` | ${a.milestoneId}/${a.sliceId}/${a.taskId} | ${(a.oneLiner || '').substring(0, 60)}`;
  } else if (c.tool === 'gsd_requirement_update') {
    summary += ` | ${a.id} | ${a.status || 'updated'}`;
  } else if (c.tool === 'gsd_complete_slice') {
    summary += ` | ${a.milestoneId}/${a.sliceId} | ${(a.oneLiner || '').substring(0, 60)}`;
  } else if (c.tool === 'gsd_validate_milestone') {
    summary += ` | ${a.milestoneId} | ${a.verdict}`;
  } else if (c.tool === 'gsd_complete_milestone') {
    summary += ` | ${a.milestoneId} | ${a.title} | passed=${a.verificationPassed}`;
  } else if (c.tool === 'gsd_checkpoint_db') {
    summary += ' | checkpoint';
  }
  
  console.log(summary);
}
