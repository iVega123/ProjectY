// This file is mounted only in the dedicated benchmark stack.
const rentalDatabase = db.getSiblingDB("RentalOperationsDB");
for (const collection of rentalDatabase.getCollectionNames()) {
  rentalDatabase.getCollection(collection).deleteMany({});
}

// Creating a rental now decides from the local rider projection instead of
// calling rider-manager. This stack seeds its rider straight into Postgres, so
// no write ever passes through the outbox and no event ever reaches the
// projection — the benchmark would warm up against "awaiting processing".
// The projection is state a rental needs, so the fixture seeds it the same way
// it seeds the rider and the motorcycles.
//
// VerifiedAtMs is NumberLong on purpose: the field is a C# long, and a bare
// mongosh number would arrive as a double and fail to deserialise. The string
// argument avoids the precision warning mongosh raises for a bare number.
rentalDatabase.getCollection("RiderProjection").replaceOne(
  { _id: "load-rider" },
  { _id: "load-rider", Verified: true, VerifiedAtMs: NumberLong("1"), Name: "Load rider" },
  { upsert: true }
);
