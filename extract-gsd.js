#!/usr/bin/env node
// Extract all GSD tool calls from respectacle session logs in chronological order
// Tool calls are nested inside message entries: type="message", message.role="assistant", 
// message.content[].type="toolCall"

const fs = require('fs');
const path = require('path');

const SESSION_DIR = 'C:/Users/david.keymel/.gsd/sessions/--C--Users-david.keymel-Documents-Projects-respectacle--';

const files = fs.readdirSync(SESSION_DIR)
  .filter(f => f.endsWith('.jsonl'))
  .sort();

const allCalls = [];

for (const file of files) {
  const filePath = path.join(SESSION_DIR, file);
  const content = fs.readFileSync(filePath, 'utf8');
  const lines = content.split('\n');

  for (const line of lines) {
    if (!line.trim()) continue;
    try {
      const obj = JSON.parse(line);
      if (obj.type !== 'message' || obj.message?.role !== 'assistant') continue;
      const timestamp = obj.timestamp || '';
      const items = obj.message?.content || [];
      
      for (const item of items) {
        const name = item.name || '';
        if (!name.startsWith('gsd_')) continue;
        
        const args = item.arguments || {};
        allCalls.push({ timestamp, tool: name, args });
      }
    } catch(e) {}
  }
}

// Write to file
fs.writeFileSync('gsd-replay.json', JSON.stringify(allCalls, null, 2));
console.error(`Extracted ${allCalls.length} GSD tool calls`);

// Print summary
const byTool = {};
for (const c of allCalls) {
  byTool[c.tool] = (byTool[c.tool] || 0) + 1;
}
console.error('By tool:');
for (const [tool, count] of Object.entries(byTool).sort()) {
  console.error(`  ${tool}: ${count}`);
}
