import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const dir = path.resolve(import.meta.dirname, 'api');
const read = async (name) => JSON.parse(await fs.readFile(path.join(dir, name), 'utf8'));
const ajv = new Ajv2020({ strict: true, allErrors: true });
addFormats(ajv);
const validators = {
  request: ajv.compile(await read('request.schema.json')),
  alert: ajv.compile(await read('alert.schema.json')),
  movies: ajv.compile(await read('movies.schema.json')),
  technical: ajv.compile(await read('technical.schema.json')),
};
const index = await read('index.json');
const expectedFiles = [
  'request-v2-invalid-old.json', 'request-v2-valid-en.json', 'request-v2-valid-sr.json',
  'request-v2-invalid-missing-language.json', 'request-v2-invalid-extra.json', 'request-v2-invalid-language.json',
  'response-v2-invalid-en.json', 'response-v2-unclear-sr.json', 'response-v2-not-movie-en.json',
  'response-v2-unsupported-sr.json', 'response-v2-no-results-en.json', 'response-v2-no-results-sr.json', 'response-v2-invalid-alert.json',
  'response-v2-movies-one.json', 'response-v2-movies-nine.json', 'response-v2-movies-ten.json',
  'response-v2-invalid-zero-movies.json', 'response-v2-invalid-eleven-movies.json',
  'response-v2-invalid-missing-poster.json', 'response-v2-invalid-duplicate-movies.json',
  'response-v2-rate-limited.json', 'response-v2-parser-invalid.json', 'response-v2-provider-unavailable.json',
  'response-v2-search-unavailable.json', 'response-v2-internal-error.json', 'response-v2-invalid-technical.json',
];
assert.equal(index.contract, 'v2.0.0');
assert.equal(index.cases.length, 26);
assert.deepEqual(index.cases.map((c) => c.file), expectedFiles, 'active index must cover the complete prescribed v2 fixture set in order');
assert.equal(new Set(expectedFiles).size, expectedFiles.length);

const alertText = {
  INVALID_REQUEST: { sr: 'Unesi ispravan zahtev za filmove.', en: 'Enter a valid movie request.' },
  QUERY_UNCLEAR: { sr: 'Napiši malo jasnije kakav film tražiš.', en: 'Describe the movie you want more clearly.' },
  NOT_MOVIE_REQUEST: { sr: 'Napiši zahtev za preporuku filma.', en: 'Enter a movie recommendation request.' },
  UNSUPPORTED_REQUEST: { sr: 'Jedan obavezan uslov trenutno ne možemo pouzdano da proverimo. Izmeni upit.', en: 'We cannot reliably check one required condition yet. Please revise your request.' },
  NO_RESULTS: { sr: 'Nema filmova koji ispunjavaju sve obavezne uslove.', en: 'No movies meet all the required conditions.' },
};
const statusByCode = { INVALID_REQUEST: 400, QUERY_UNCLEAR: 422, NOT_MOVIE_REQUEST: 422, UNSUPPORTED_REQUEST: 422, NO_RESULTS: 200, RATE_LIMITED: 429, PARSER_INVALID_RESPONSE: 502, PROVIDER_UNAVAILABLE: 503, SEARCH_UNAVAILABLE: 503, INTERNAL_ERROR: 500 };

for (const entry of index.cases) {
  assert.deepEqual(Object.keys(entry).sort(), ['contractValid','file','httpStatus','schema','schemaValid', ...(entry.expectedCode ? ['expectedCode'] : []), ...(entry.language ? ['language'] : []), ...(entry.expectedCount !== undefined ? ['expectedCount'] : []), ...(entry.expectedPartial !== undefined ? ['expectedPartial'] : [])].sort(), `${entry.file}: exact index fields`);
  const value = await read(entry.file);
  const schemaValid = validators[entry.schema](value);
  assert.equal(schemaValid, entry.schemaValid, `${entry.file}: ${JSON.stringify(validators[entry.schema].errors)}`);
  let contractValid;
  if (entry.schema === 'request') {
    contractValid = schemaValid;
    assert.equal(entry.httpStatus, null);
  } else if (entry.schema === 'alert') {
    const { alert } = value;
    contractValid = value.type === 'alert' && Object.keys(value).sort().join(',') === 'alert,type' && alert.code === entry.expectedCode && alert.message === alertText[entry.expectedCode]?.[entry.language];
    if (entry.expectedCode) {
      assert.equal(entry.httpStatus, statusByCode[entry.expectedCode]);
      assert.equal(alert.message, alertText[entry.expectedCode]?.[entry.language], `${entry.file}: exact localized copy`);
    }
  } else if (entry.schema === 'technical') {
    const code = value.error?.code;
    contractValid = Object.keys(value).join(',') === 'error' && code === entry.expectedCode && Object.keys(value.error).join(',') === 'code';
    if (entry.expectedCode) assert.equal(entry.httpStatus, statusByCode[entry.expectedCode], `${entry.file}: status for code`);
  } else {
    const cards = value.movies;
    const count = cards.length;
    const partial = count >= 1 && count <= 9;
    const uniqueIds = new Set(cards.map((card) => card.imdbUrl)).size === count;
    contractValid = schemaValid && count >= 1 && count <= 10 && value.meta?.count === count && value.meta?.partial === partial && uniqueIds;
    if (entry.expectedCount !== undefined) assert.equal(count, entry.expectedCount, `${entry.file}: movies count`);
    if (entry.expectedPartial !== undefined) assert.equal(value.meta.partial, entry.expectedPartial, `${entry.file}: partial`);
    if (count > 0) assert.equal(value.meta.count, count, `${entry.file}: meta count`);
    if (count >= 1 && count <= 10) assert.equal(value.meta.partial, partial, `${entry.file}: exact partial rule`);
    if (entry.httpStatus !== null) assert.equal(entry.httpStatus, 200);
  }
  assert.equal(contractValid, entry.contractValid, `${entry.file}: contract invariants`);
  if (!entry.contractValid) assert.equal(entry.httpStatus, null, `${entry.file}: invalid examples have no success status`);
}
console.log(`Validated ${index.cases.length} active v2 API fixtures: request, alert, movies, and technical envelopes plus wire-level invariants.`);
