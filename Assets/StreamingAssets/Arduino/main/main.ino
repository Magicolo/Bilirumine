int inputs[4] = { 2, 5, 8, 11 };
int outputs[4] = { 3, 6, 9, 12 };
int lights[4] = { 4, 7, 10, 13 };
byte read[1] = { 0 };
byte write[4] = { 0, 0, 0, 0 };

void setup()
{
  Serial.begin(9600);
  for (int i = 0; i < 4; i++) {
    pinMode(inputs[i], INPUT_PULLUP); 
    pinMode(outputs[i], OUTPUT);
    pinMode(lights[i], OUTPUT); 
  }
}

void loop()
{
  while (Serial.readBytes(read, 1) == 0) delay(1);

  for (int i = 0; i < 4; i++) {
    digitalWrite(outputs[i], LOW); 
  }

  for (int i = 0; i < 4; i++) {
    write[i] = digitalRead(inputs[i]) == 0;
    digitalWrite(lights[i], read[0] == 0 || write[i] ? LOW : HIGH);
  }

  Serial.write(write, 4);
  Serial.flush();
}