// This file is mounted only in the dedicated benchmark stack.
const rentalDatabase = db.getSiblingDB("RentalOperationsDB");
for (const collection of rentalDatabase.getCollectionNames()) {
  rentalDatabase.getCollection(collection).deleteMany({});
}
